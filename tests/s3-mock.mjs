#!/usr/bin/env node
// A stand-in for S3, for testing the bucket-backed state without a bucket.
//
//   node tests/s3-mock.mjs --dir /tmp/bucket --port 9300 [--bucket any]
//
// It speaks just enough of the REST API for gamer.farm's Core/S3Store.cs - PUT, GET, DELETE, and ListObjectsV2 - and it
// VERIFIES the SigV4 signature instead of ignoring it, so a signer that builds the canonical request wrong is caught
// here rather than in production ("SignatureDoesNotMatch" is what a real bucket says, and this says the same).
//
// The signer it verifies against is checked against AWS's own published example at startup: if that vector doesn't
// come out as the documented signature, the mock refuses to run rather than pass anything.

import http from 'node:http';
import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';

// ── args ────────────────────────────────────────────────────────────────────
const args = process.argv.slice(2);
const arg = (name, fallback) => {
	const at = args.indexOf(`--${name}`);
	return at >= 0 && at + 1 < args.length ? args[at + 1] : fallback;
};

const dir = path.resolve(arg('dir', '/tmp/s3-mock-bucket'));
const port = Number(arg('port', '9300'));
fs.mkdirSync(dir, { recursive: true });

// ── AWS SigV4, exactly as the spec writes it ────────────────────────────────

const uriEncode = (s) => encodeURIComponent(s).replace(/[!'()*]/g, (c) => '%' + c.charCodeAt(0).toString(16).toUpperCase());
const sha256 = (b) => crypto.createHash('sha256').update(b).digest('hex');
const hmac = (key, data) => crypto.createHmac('sha256', key).update(data).digest();

function signingKey(secret, date, region, service) {
	return hmac(hmac(hmac(hmac('AWS4' + secret, date), region), service), 'aws4_request');
}

function canonicalQuery(query) {
	const pairs = [];
	for (const [k, v] of new URLSearchParams(query)) pairs.push([uriEncode(k), uriEncode(v)]);
	pairs.sort((a, b) => (a[0] < b[0] ? -1 : a[0] > b[0] ? 1 : 0));
	return pairs.map(([k, v]) => `${k}=${v}`).join('&');
}

function sign({ secret, region, amzDate, method, uri, query, headers, signedHeaders, payloadHash }) {
	const date = amzDate.slice(0, 8);
	const canonicalHeaders = signedHeaders.split(';').map((h) => `${h}:${String(headers[h] ?? '').trim().replace(/\s+/g, ' ')}`).join('\n') + '\n';
	const canonicalRequest = [method, uri, canonicalQuery(query), canonicalHeaders, signedHeaders, payloadHash].join('\n');
	const scope = `${date}/${region}/s3/aws4_request`;
	const stringToSign = ['AWS4-HMAC-SHA256', amzDate, scope, sha256(canonicalRequest)].join('\n');

	return { signature: hmac(signingKey(secret, date, region, 's3'), stringToSign).toString('hex'), scope };
}

// AWS's published S3 example ("Example: GET Object"): if this doesn't match, the verifier is wrong, not the client.
{
	const empty = 'e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855';
	const { signature } = sign({
		secret: 'wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY',
		region: 'us-east-1',
		amzDate: '20130524T000000Z',
		method: 'GET',
		uri: '/test.txt',
		query: '',
		headers: {
			host: 'examplebucket.s3.amazonaws.com',
			range: 'bytes=0-9',
			'x-amz-content-sha256': empty,
			'x-amz-date': '20130524T000000Z'
		},
		signedHeaders: 'host;range;x-amz-content-sha256;x-amz-date',
		payloadHash: empty
	});

	if (signature !== 'f0e8bdb87c964420e857bd35b5d6ed310bd44f0170aba48dd91039c6036bdb41') {
		console.error(`the SigV4 verifier is wrong: AWS's example signs to ${signature}`);
		process.exit(1);
	}
}

// ── the store on disk ──────────────────────────────────────────────────────

const keyFile = (bucket, key) => path.join(dir, bucket, key);
const safe = (key) => !key.split('/').some((p) => p === '..' || p === '');

function listObjects(bucket, prefix, token, maxKeys) {
	const root = path.join(dir, bucket);
	const found = [];

	const walk = (sub) => {
		for (const entry of fs.readdirSync(path.join(root, sub), { withFileTypes: true })) {
			const rel = sub ? `${sub}/${entry.name}` : entry.name;
			if (entry.isDirectory()) walk(rel);
			else found.push(rel);
		}
	};

	if (fs.existsSync(root)) walk('');

	return found
		.filter((k) => k.startsWith(prefix))
		.sort()
		.filter((k) => !token || k > token)
		.slice(0, maxKeys);
}

const xml = (s) => String(s).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');

function error(res, status, code, message) {
	res.writeHead(status, { 'Content-Type': 'application/xml' });
	res.end(`<?xml version="1.0" encoding="UTF-8"?><Error><Code>${code}</Code><Message>${xml(message)}</Message></Error>`);
}

// ── the server ─────────────────────────────────────────────────────────────

const server = http.createServer((req, res) => {
	const chunks = [];
	req.on('data', (c) => chunks.push(c));
	req.on('end', () => {
		const body = Buffer.concat(chunks);
		const [rawPath, query] = req.url.split('?');
		const [, bucket, ...rest] = rawPath.split('/');
		const key = rest.join('/');

		// credentials: "test" holds no secrets, so the mock takes them from the Authorization header itself and trusts
		// the pair it was told to (--access-key / --secret-key), which default to the ones the docs/CI use.
		const auth = req.headers.authorization ?? '';
		const m = /^AWS4-HMAC-SHA256 Credential=([^/]+)\/(\d{8})\/([^/]+)\/s3\/aws4_request, SignedHeaders=([^,]+), Signature=([0-9a-f]{64})$/.exec(auth);

		if (!m) {
			error(res, 403, 'AccessDenied', 'missing or malformed Authorization');
			return;
		}

		const [, accessKey, date, region, signedHeaders, signature] = m;
		const accessExpected = arg('access-key', 'test');
		const secretKey = arg('secret-key', 'test-secret');
		const payloadHash = req.headers['x-amz-content-sha256'] ?? sha256(body);

		if (accessKey !== accessExpected || date !== (req.headers['x-amz-date'] ?? '').slice(0, 8)) {
			error(res, 403, 'InvalidAccessKeyId', `unknown access key ${accessKey}`);
			return;
		}

		if (sha256(body) !== payloadHash && payloadHash !== 'UNSIGNED-PAYLOAD') {
			error(res, 400, 'XAmzContentSHA256Mismatch', 'the body does not hash to x-amz-content-sha256');
			return;
		}

		const headers = {};
		for (const name of signedHeaders.split(';')) headers[name] = req.headers[name] ?? '';

		const expected = sign({
			secret: secretKey,
			region,
			amzDate: req.headers['x-amz-date'] ?? '',
			method: req.method,
			uri: rawPath,
			query: query ?? '',
			headers,
			signedHeaders,
			payloadHash
		}).signature;

		if (expected !== signature) {
			error(res, 403, 'SignatureDoesNotMatch', `expected ${expected}`);
			return;
		}

		const isList = rest.length === 0 && (query ?? '').includes('list-type=2');

		if (isList) {
			const params = new URLSearchParams(query);
			const prefix = params.get('prefix') ?? '';
			const token = params.get('continuation-token') ?? '';
			const maxKeys = Math.min(Number(params.get('max-keys') ?? '1000') || 1000, 1000);
			const keys = listObjects(bucket, prefix, token, maxKeys + 1);
			const page = keys.slice(0, maxKeys);
			const more = keys.length > maxKeys;

			const contents = page.map((k) => {
				const stat = fs.statSync(keyFile(bucket, k));
				return `<Contents><Key>${xml(k)}</Key><LastModified>${stat.mtime.toISOString()}</LastModified>`
					+ `<ETag>&quot;${crypto.createHash('md5').update(fs.readFileSync(keyFile(bucket, k))).digest('hex')}&quot;</ETag>`
					+ `<Size>${stat.size}</Size><StorageClass>STANDARD</StorageClass></Contents>`;
			}).join('');

			res.writeHead(200, { 'Content-Type': 'application/xml' });
			res.end('<?xml version="1.0" encoding="UTF-8"?>'
				+ '<ListBucketResult xmlns="http://s3.amazonaws.com/doc/2006-03-01/">'
				+ `<Name>${xml(bucket)}</Name><Prefix>${xml(prefix)}</Prefix><KeyCount>${page.length}</KeyCount>`
				+ `<MaxKeys>${maxKeys}</MaxKeys><IsTruncated>${more}</IsTruncated>${contents}`
				+ (more ? `<NextContinuationToken>${xml(page[page.length - 1])}</NextContinuationToken>` : '')
				+ '</ListBucketResult>');

			console.log(`${req.method} /${bucket}?list-type=2 prefix=${prefix} → ${page.length} object(s)${more ? ' (more)' : ''}`);

			return;
		}

		if (!safe(key)) {
			error(res, 400, 'InvalidObjectName', 'key escapes the bucket');
			return;
		}

		const file = keyFile(bucket, key);

		if (req.method === 'PUT') {
			fs.mkdirSync(path.dirname(file), { recursive: true });
			fs.writeFileSync(file, body);
			console.log(`PUT ${key} (${body.length} bytes)`);
			res.writeHead(200, { ETag: `"${crypto.createHash('md5').update(body).digest('hex')}"` });
			res.end();
			return;
		}

		if (req.method === 'GET') {
			if (!fs.existsSync(file)) {
				console.log(`GET ${key} → 404`);
				error(res, 404, 'NoSuchKey', `no such key: ${key}`);
				return;
			}

			const data = fs.readFileSync(file);
			console.log(`GET ${key} (${data.length} bytes)`);
			res.writeHead(200, { 'Content-Type': 'application/octet-stream', 'Content-Length': data.length });
			res.end(data);
			return;
		}

		if (req.method === 'DELETE') {
			if (fs.existsSync(file)) fs.rmSync(file);
			console.log(`DELETE ${key}`);
			res.writeHead(204);
			res.end();
			return;
		}

		error(res, 405, 'MethodNotAllowed', `${req.method} is not implemented here`);
	});
});

server.listen(port, '127.0.0.1', () => {
	console.log(`s3-mock: http://127.0.0.1:${port} → ${dir} (SigV4 verified)`);
});