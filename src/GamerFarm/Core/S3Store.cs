using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace GamerFarm.Core;

// ─────────────────────────────────────────────────────────────────────────────
//  A bucket, spoken to directly: GET, PUT, DELETE and LIST, SigV4-signed.
//
//  Why no SDK: gamer.farm ships three NuGet packages and a 100-line signer is smaller than the AWS SDK's dependency
//  tree - and it works with every S3-compatible store (AWS, Cloudflare R2, Backblaze B2's S3 API, MinIO, Ceph,
//  Garage), not only AWS's. Everything here speaks the documented S3 REST API, so a store that passes its own
//  conformance tests passes ours.
//
//  Signing is AWS Signature Version 4 with the payload hash actually computed ("x-amz-content-sha256"), which every
//  S3 implementation requires; "UNSIGNED-PAYLOAD" is only accepted over TLS and not by all of them.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Where the state lives, read from the environment and nowhere else (see <see cref="FromEnvironment"/>).</summary>
public sealed class S3Options {
	/// <summary>The bucket. Empty means "no bucket configured" and the whole feature stays off.</summary>
	public string Bucket { get; init; } = "";

	/// <summary>The key prefix every object sits under, without a trailing slash. Empty puts them at the bucket's root.</summary>
	public string Prefix { get; init; } = "";

	public string Region { get; init; } = "us-east-1";

	/// <summary>"https://s3.eu-central-1.amazonaws.com", "https://&lt;account&gt;.r2.cloudflarestorage.com", "http://minio:9000".</summary>
	public Uri Endpoint { get; init; } = new("https://s3.amazonaws.com");

	/// <summary>Path style ("/bucket/key") instead of the bucket in the host name. MinIO, R2 and most self-hosted need it.</summary>
	public bool PathStyle { get; init; }

	public string AccessKey { get; init; } = "";

	public string SecretKey { get; init; } = "";

	/// <summary>A temporary credential's session token (STS, an assumed role, R2's dashboard tokens).</summary>
	public string? SessionToken { get; init; }

	public bool Configured => Bucket.Length > 0 && AccessKey.Length > 0 && SecretKey.Length > 0;

	/// <summary>A readable name for the log: "bucket/prefix".</summary>
	public string Where => Prefix.Length > 0 ? $"{Bucket}/{Prefix}" : Bucket;

	private static string? Env(string name) {
		string? value = Environment.GetEnvironmentVariable(name);

		return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
	}

	/// <summary>
	/// The environment, in the order the rest of the app reads it: GAMERFARM_S3_* first, then the names every S3 tool
	/// already uses (AWS_ACCESS_KEY_ID and friends), so a container that is already given AWS credentials needs no more
	/// than the bucket's name.
	/// </summary>
	/// <remarks>
	///   GAMERFARM_S3_BUCKET            the bucket - set this and the state moves to S3
	///   GAMERFARM_S3_PREFIX           key prefix (default "gamerfarm")
	///   GAMERFARM_S3_REGION           default us-east-1
	///   GAMERFARM_S3_ENDPOINT         for anything that isn't AWS (R2, B2, MinIO, Ceph)
	///   GAMERFARM_S3_ACCESS_KEY_ID    or AWS_ACCESS_KEY_ID
	///   GAMERFARM_S3_SECRET_ACCESS_KEY or AWS_SECRET_ACCESS_KEY
	///   GAMERFARM_S3_SESSION_TOKEN    or AWS_SESSION_TOKEN
	///   GAMERFARM_S3_PATH_STYLE       1/0 - default: on when an endpoint is set (MinIO and R2 are path style)
	///   each of the four credentials also in a _FILE variant, for a Docker or Kubernetes secret file.
	/// </remarks>
	public static S3Options? FromEnvironment() {
		string? bucket = Env("GAMERFARM_S3_BUCKET") ?? Env("S3_BUCKET");

		if (bucket == null) {
			return null;
		}

		string region = Env("GAMERFARM_S3_REGION") ?? Env("AWS_REGION") ?? Env("AWS_DEFAULT_REGION") ?? "us-east-1";
		string? endpoint = Env("GAMERFARM_S3_ENDPOINT");

		Uri uri = endpoint != null
			? (Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? parsed) && (parsed.Scheme is "http" or "https")
				? parsed
				: throw new InvalidOperationException($"GAMERFARM_S3_ENDPOINT isn't an http(s) address: {endpoint}"))
			: new Uri(region == "us-east-1" ? "https://s3.amazonaws.com" : $"https://s3.{region}.amazonaws.com");

		string prefix = (Env("GAMERFARM_S3_PREFIX") ?? "gamerfarm").Trim('/');

		string? pathStyle = Env("GAMERFARM_S3_PATH_STYLE");

		return new S3Options {
			Bucket = bucket,
			Prefix = prefix,
			Region = region,
			Endpoint = uri,
			// A bucket in the host name only works on AWS (and needs the bucket to be DNS-safe): anything with its own
			// endpoint gets path style unless it says otherwise.
			PathStyle = pathStyle != null ? pathStyle is "1" or "true" or "yes" : endpoint != null,
			AccessKey = Secret(Env("GAMERFARM_S3_ACCESS_KEY_ID") ?? Env("AWS_ACCESS_KEY_ID") ?? "", "GAMERFARM_S3_ACCESS_KEY_FILE"),
			SecretKey = Secret(Env("GAMERFARM_S3_SECRET_ACCESS_KEY") ?? Env("AWS_SECRET_ACCESS_KEY") ?? "", "GAMERFARM_S3_SECRET_KEY_FILE"),
			SessionToken = Secret(Env("GAMERFARM_S3_SESSION_TOKEN") ?? Env("AWS_SESSION_TOKEN") ?? "", "GAMERFARM_S3_SESSION_TOKEN_FILE")
		};
	}

	/// <summary>A value, or the contents of the file its _FILE variable names (Kubernetes secrets, Docker secrets).</summary>
	private static string Secret(string value, string fileVar) {
		if (value.Length > 0) {
			return value;
		}

		if (Env(fileVar) is not { } path) {
			return "";
		}

		try {
			return File.ReadAllText(path).Trim();
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			Log.Warn(new Said("cloud: couldn't read {0} ({1})", fileVar, Log.Describe(e)));

			return "";
		}
	}
}

/// <summary>One object in the bucket, as a listing returns it.</summary>
public readonly record struct S3Entry(string Key, long Size, DateTime LastModified);

/// <summary>What the store said. The status is kept so a caller can tell "no such key" (404) from "wrong credentials" (403).</summary>
public sealed class S3Exception(int status, string code, string message) : Exception($"S3 {status} {code}: {message}") {
	public int Status { get; } = status;

	public string Code { get; } = code;

	public bool Missing => Status == 404;
}

/// <summary>The four calls the state store needs, over SigV4. One instance per process; every call is retried on a 5xx or a timeout.</summary>
public sealed class S3Store : IDisposable {
	private const string EmptyHash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

	private static readonly TimeSpan[] Backoff = [TimeSpan.FromMilliseconds(400), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)];

	private readonly S3Options _o;
	private readonly HttpClient _http;

	public S3Store(S3Options options) {
		_o = options;

		SocketsHttpHandler handler = new() {
			PooledConnectionLifetime = TimeSpan.FromMinutes(10),
			ConnectTimeout = TimeSpan.FromSeconds(10),
			AllowAutoRedirect = false,
			AutomaticDecompression = DecompressionMethods.None
		};

		_http = new HttpClient(handler, disposeHandler: true) {
			// A 30 MB backup zip on a slow uplink is minutes; the caller's own cancellation is the real limit.
			Timeout = TimeSpan.FromMinutes(10)
		};
		_http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "gamer.farm/" + Build.Version);
	}

	public S3Options Options => _o;

	public void Dispose() => _http.Dispose();

	/// <summary>The object key for a path relative to the data folder: "config/gamerFarm.json" → "prefix/config/gamerFarm.json".</summary>
	public string Key(string relative) => _o.Prefix.Length > 0 ? $"{_o.Prefix}/{relative}" : relative;

	// ── the calls ────────────────────────────────────────────────────────────

	/// <summary>Every object under <paramref name="prefix"/>, a page at a time, until the store says there are no more.</summary>
	public async Task<List<S3Entry>> ListAsync(string prefix, CancellationToken ct) {
		List<S3Entry> all = [];
		string? token = null;

		for (int page = 0; page < 100; page++) {
			Dictionary<string, string> query = new() { ["list-type"] = "2", ["prefix"] = prefix, ["max-keys"] = "1000" };

			if (token is { Length: > 0 }) {
				query["continuation-token"] = token;
			}

			byte[] body = await SendAsync(HttpMethod.Get, "", query, null, "application/xml", ct).ConfigureAwait(false);
			XElement? root = Parse(body).Root;

			if (root == null) {
				break;
			}

			// Local names, no namespace: AWS and MinIO send xmlns="http://s3.amazonaws.com/doc/2006-03-01/", and
			// a store that leaves it out is still readable this way.
			foreach (XElement content in root.Elements().Where(static e => e.Name.LocalName == "Contents")) {
				string key = Child(content, "Key");

				if (key.Length == 0) {
					continue;
				}

				long size = long.TryParse(Child(content, "Size"), out long s) ? s : -1;
				DateTime when = DateTime.TryParse(
					Child(content, "LastModified"),
					System.Globalization.CultureInfo.InvariantCulture,
					System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
					out DateTime w) ? w : DateTime.MinValue;

				all.Add(new S3Entry(key, size, when));
			}

			bool more = string.Equals(Child(root, "IsTruncated"), "true", StringComparison.OrdinalIgnoreCase);
			token = Child(root, "NextContinuationToken");

			if (!more || token.Length == 0) {
				break;
			}
		}

		return all;
	}

	/// <summary>A child element's text by local name, "" when it isn't there.</summary>
	private static string Child(XElement parent, string name) =>
		parent.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value ?? "";

	/// <summary>The object's bytes, or null when it isn't there.</summary>
	public async Task<byte[]?> GetAsync(string key, CancellationToken ct) {
		try {
			return await SendAsync(HttpMethod.Get, key, null, null, "application/octet-stream", ct).ConfigureAwait(false);
		} catch (S3Exception e) when (e.Missing) {
			return null;
		}
	}

	public Task PutAsync(string key, byte[] body, CancellationToken ct) => SendAsync(HttpMethod.Put, key, null, body, "application/octet-stream", ct);

	public Task DeleteAsync(string key, CancellationToken ct) => SendAsync(HttpMethod.Delete, key, null, null, "application/octet-stream", ct);

	// ── talking to the store ─────────────────────────────────────────────────

	private async Task<byte[]> SendAsync(HttpMethod method, string key, Dictionary<string, string>? query, byte[]? body, string contentType, CancellationToken ct) {
		for (int attempt = 0; ; attempt++) {
			try {
				return await OnceAsync(method, key, query, body, contentType, ct).ConfigureAwait(false);
			} catch (Exception e) when (Retryable(e, ct) && (attempt < Backoff.Length)) {
				Log.Debug($"cloud: {method.Method} {key} failed ({Log.Describe(e)}), trying again");
			}

			await Task.Delay(Backoff[attempt], ct).ConfigureAwait(false);
		}
	}

	/// <summary>A dropped connection, a timeout (not the caller's own cancellation), or the store being briefly busy.</summary>
	private static bool Retryable(Exception e, CancellationToken ct) {
		if (ct.IsCancellationRequested) {
			return false;
		}

		return e switch {
			S3Exception s3 => !s3.Missing && s3.Status is 429 or 500 or 502 or 503 or 504,
			HttpRequestException or IOException or TaskCanceledException => true,
			_ => false
		};
	}

	private async Task<byte[]> OnceAsync(HttpMethod method, string key, Dictionary<string, string>? query, byte[]? body, string contentType, CancellationToken ct) {
		Uri uri = BuildUri(key, query);
		string amzDate = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'");
		string dateStamp = amzDate[..8];
		byte[] payload = body ?? [];
		string payloadHash = body == null ? EmptyHash : Hex(SHA256.HashData(payload));

		StringBuilder canonical = new();
		canonical.Append(method.Method).Append('\n').Append(uri.AbsolutePath).Append('\n');
		canonical.Append(CanonicalQuery(query)).Append('\n');
		canonical.Append("host:").Append(uri.Authority).Append('\n');
		canonical.Append("x-amz-content-sha256:").Append(payloadHash).Append('\n');
		canonical.Append("x-amz-date:").Append(amzDate).Append('\n');

		// The headers block ends with a newline of its own, and the format puts another one after it - the blank line
		// before SignedHeaders in AWS's own example. Miss it and every signature is off by one byte.
		canonical.Append('\n');

		string signed = _o.SessionToken is { Length: > 0 }
			? "host;x-amz-content-sha256;x-amz-date;x-amz-security-token"
			: "host;x-amz-content-sha256;x-amz-date";

		canonical.Append(signed).Append('\n').Append(payloadHash);

		string scope = $"{dateStamp}/{_o.Region}/s3/aws4_request";
		string toSign = $"AWS4-HMAC-SHA256\n{amzDate}\n{scope}\n{Hex(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())))}";

		byte[] signingKey = Hmac(
			Hmac(Hmac(Hmac(Encoding.UTF8.GetBytes("AWS4" + _o.SecretKey), dateStamp), _o.Region), "s3"),
			"aws4_request");
		string signature = Hex(Hmac(signingKey, toSign));

		using HttpRequestMessage request = new(method, uri);
		request.Headers.TryAddWithoutValidation("x-amz-date", amzDate);
		request.Headers.TryAddWithoutValidation("x-amz-content-sha256", payloadHash);

		if (_o.SessionToken is { Length: > 0 } token) {
			request.Headers.TryAddWithoutValidation("x-amz-security-token", token);
		}

		request.Headers.TryAddWithoutValidation(
			"Authorization",
			$"AWS4-HMAC-SHA256 Credential={_o.AccessKey}/{scope}, SignedHeaders={signed}, Signature={signature}");

		if (body != null) {
			ByteArrayContent content = new(body);
			content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
			request.Content = content;
		}

		using HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
		byte[] answer = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);

		if (response.IsSuccessStatusCode) {
			return answer;
		}

		(string code, string message) = DescribeError(answer, response.ReasonPhrase ?? "");

		throw new S3Exception((int) response.StatusCode, code, message);
	}

	/// <summary>The store's own &lt;Code&gt; and &lt;Message&gt;, when the error body is the XML it sends; the reason phrase otherwise.</summary>
	private static (string Code, string Message) DescribeError(byte[] body, string fallback) {
		try {
			XElement? root = Parse(body).Root;

			if (root == null) {
				return ("HTTP", fallback);
			}

			string code = root.Elements().FirstOrDefault(static e => e.Name.LocalName == "Code")?.Value ?? "HTTP";
			string message = root.Elements().FirstOrDefault(static e => e.Name.LocalName == "Message")?.Value ?? fallback;

			return (code, message);
		} catch (Exception) {
			return ("HTTP", body.Length > 0 ? Encoding.UTF8.GetString(body, 0, Math.Min(body.Length, 200)) : fallback);
		}
	}

	private Uri BuildUri(string key, Dictionary<string, string>? query) {
		string escapedKey = Escape(key);
		string path = _o.PathStyle
			? (escapedKey.Length > 0 ? $"/{Escape(_o.Bucket)}/{escapedKey}" : $"/{Escape(_o.Bucket)}")
			: $"/{escapedKey}";
		string authority = _o.Endpoint.Authority;
		string host = _o.PathStyle ? authority : $"{_o.Bucket}.{authority}";
		string url = $"{_o.Endpoint.Scheme}://{host}{path}";

		if (query is { Count: > 0 }) {
			url += "?" + CanonicalQuery(query);
		}

		// Left exactly as built: the default Uri canonicalisation would re-escape what is already escaped, and then the
		// signature would be over a different path than the one sent.
		return new Uri(url, new UriCreationOptions { DangerousDisablePathAndQueryCanonicalization = true });
	}

	/// <summary>Percent-encoding that matches what S3 signs: every segment, unreserved characters only.</summary>
	private static string Escape(string value) => string.Join("/", value.Split('/').Select(Uri.EscapeDataString));

	private static string CanonicalQuery(Dictionary<string, string>? query) =>
		query == null || query.Count == 0
			? ""
			: string.Join("&", query
				.Select(static p => (Name: Uri.EscapeDataString(p.Key), Value: Uri.EscapeDataString(p.Value)))
				.OrderBy(static p => p.Name, StringComparer.Ordinal)
				.Select(static p => $"{p.Name}={p.Value}"));

	private static XDocument Parse(byte[] body) {
		using MemoryStream stream = new(body);
		return XDocument.Load(stream);
	}

	private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

	private static byte[] Hmac(byte[] key, string data) => HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(data));
}