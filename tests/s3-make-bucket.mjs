#!/usr/bin/env node
// Makes a bucket, because a CI job needs one before the app can use it.
//
//   node tests/s3-make-bucket.mjs --endpoint http://127.0.0.1:9000 --bucket mybucket \
//        --access-key test --secret-key test-secret [--region us-east-1]
//
// SigV4-signed PUT on /<bucket> - that is what "create bucket" is in the S3 REST API. "BucketAlreadyOwnedByYou" (409)
// counts as success, so the script can be run twice.

import crypto from 'node:crypto';

const args = process.argv.slice(2);
const arg = (name, fallback) => {
	const at = args.indexOf(`--${name}`);
	return at >= 0 && at + 1 < args.length ? args[at + 1] : fallback;
};

const endpoint = arg('endpoint', 'http://127.0.0.1:9000').replace(/\/+$/, '');
const bucket = arg('bucket', 'gamerfarm');
const accessKey = arg('access-key', 'test');
const secretKey = arg('secret-key', 'test-secret');
const region = arg('region', 'us-east-1');

const sha256 = (b) => crypto.createHash('sha256').update(b).digest('hex');
const hmac = (key, data) => crypto.createHmac('sha256', key).update(data).digest();

const url = new URL(`${endpoint}/${bucket}`);
const amzDate = new Date().toISOString().replace(/[:-]|\.\d{3}/g, '');
const dateStamp = amzDate.slice(0, 8);
const empty = sha256('');

// Every header named in SignedHeaders has to be in the canonical headers block, in that order, and the block's own
// trailing newline plus the separator's makes the blank line before SignedHeaders.
const canonicalRequest = [
	'PUT',
	url.pathname,
	'',
	`host:${url.host}\nx-amz-content-sha256:${empty}\nx-amz-date:${amzDate}\n`,
	'host;x-amz-content-sha256;x-amz-date',
	empty
].join('\n');

const scope = `${dateStamp}/${region}/s3/aws4_request`;
const stringToSign = ['AWS4-HMAC-SHA256', amzDate, scope, sha256(Buffer.from(canonicalRequest))].join('\n');
const signingKey = hmac(hmac(hmac(hmac('AWS4' + secretKey, dateStamp), region), 's3'), 'aws4_request');
const signature = crypto.createHmac('sha256', signingKey).update(stringToSign).digest('hex');

const answer = await fetch(url, {
	method: 'PUT',
	headers: {
		'x-amz-date': amzDate,
		'x-amz-content-sha256': empty,
		Authorization: `AWS4-HMAC-SHA256 Credential=${accessKey}/${scope}, SignedHeaders=host;x-amz-content-sha256;x-amz-date, Signature=${signature}`
	}
});

const body = await answer.text();

if (answer.ok || answer.status === 409 || /BucketAlreadyOwnedByYou|BucketAlreadyExists/.test(body)) {
	console.log(`bucket ${bucket} is there (${answer.status})`);
	process.exit(0);
}

console.error(`couldn't make ${bucket}: ${answer.status} ${body.slice(0, 400)}`);
process.exit(1);