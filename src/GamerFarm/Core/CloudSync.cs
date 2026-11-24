using System.Security.Cryptography;
using System.Text;
using GamerFarm.Config;

namespace GamerFarm.Core;

// ─────────────────────────────────────────────────────────────────────────────
//  The data folder, kept in a bucket.
//
//  gamer.farm keeps everything that matters in ONE folder: config/ (the settings, every account's config, their saved
//  Steam logins, the AES key that opens them, the state files the modules write) and plugins/. On a PC that folder is
//  the installation's memory. A container has no memory - re-create it and the accounts have to be signed in again,
//  every time.
//
//  With GAMERFARM_S3_BUCKET set, that folder becomes a cache of what is in the bucket:
//
//    start     LIST the prefix, pull what's there (or what's newer there), THEN let the app read it - the accounts,
//              their tokens and the key are in place before anything signs in.
//    running   every save is noticed (a watcher, plus a sweep every 30s) and PUT back, debounced so a burst of writes
//              is one round of uploads rather than twenty.
//    stopping  one last sweep inside the shutdown, so the lifetime totals a clean exit writes are not lost with it.
//
//  A file deleted here is deleted there - except when the pull didn't fully succeed, or when a whole folder vanished at
//  once (a mount that didn't come up looks exactly like "the user deleted everything"), which is said and skipped.
//
//  One writer per prefix: two copies sharing a bucket would overwrite each other's settings. The prefix
//  (GAMERFARM_S3_PREFIX, "gamerfarm" by default) is what keeps two farms apart in one bucket.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>What the state folder is kept in: S3, Cloudflare R2, Backblaze B2, MinIO, Ceph - anything with an S3 API.</summary>
public static class CloudSync {
	/// <summary>Folders of the data folder that go to the bucket by default. "backups" and "logs" can be added.</summary>
	private const string DefaultFolders = "config,plugins";

	/// <summary>Every folder that may be named in GAMERFARM_S3_FOLDERS - nothing else can be pointed at the bucket.</summary>
	private static readonly string[] AllowedFolders = ["config", "plugins", "backups", "logs"];

	/// <summary>Never: the single-instance lock belongs to the copy running now, and a pulled one would be a stale lock.</summary>
	private static readonly string[] NeverPaths = ["config/state/instance.lock"];

	private static readonly Lock Gate = new();

	/// <summary>Where the data folder is, as --path gave it.</summary>
	private static string _root = "";

	private static S3Options? _options;
	private static S3Store? _store;
	private static string[] _folders = [];

	/// <summary>Files already in the bucket as of the last sweep - path, size, write time and the hash last put there.</summary>
	private static readonly Dictionary<string, Known> KnownFiles = new(StringComparer.Ordinal);

	/// <summary>False until the pull succeeded: with no idea what the bucket holds, putting the local files over it could
	/// wipe a whole farm. A pull that failed means this run keeps its state to itself (and says so).</summary>
	private static bool _canWrite;

	/// <summary>A pull couldn't read one of the objects: a file this copy has never seen must not be taken for a deleted one.</summary>
	private static bool _canDelete;

	private static bool _required = true;
	private static bool _verbose;
	private static TimeSpan _interval = TimeSpan.FromSeconds(30);
	private static readonly TimeSpan Debounce = TimeSpan.FromSeconds(2);

	private static readonly SemaphoreSlim Wake = new(0, 4096);
	private static readonly SemaphoreSlim One = new(1, 1);
	private static CancellationTokenSource? _stop;
	private static Task? _loop;
	private static readonly List<FileSystemWatcher> Watchers = [];

	private static DateTime _lastOk = DateTime.MinValue;
	private static string _lastError = "";
	private static int _uploads, _downloads, _deletes;
	private static int _files;

	private readonly record struct Known(long Size, long Ticks, string? Hash);

	public static bool Configured => _store != null;

	/// <summary>The bucket as the log and 'cloudsync' say it: "bucket/prefix".</summary>
	public static string Where => _store?.Options.Where ?? "";

	// ── start ────────────────────────────────────────────────────────────────

	/// <summary>
	/// Reads the environment, pulls the bucket into <paramref name="root"/> and starts watching it. Nothing at all
	/// happens without GAMERFARM_S3_BUCKET: a plain install and a plain container are exactly as they were.
	/// </summary>
	/// <returns>False when the state was required and couldn't be had - the caller exits, rather than run on defaults
	/// that would be uploaded over the real settings at the first save.</returns>
	public static async Task<bool> StartAsync(string root) {
		S3Options? options;

		try {
			options = S3Options.FromEnvironment();
		} catch (Exception e) {
			Log.Error(new Said("cloud: {0}", Log.Describe(e)));

			return false;
		}

		if (options == null) {
			return true;
		}

		_lastError = "";

		if (!options.Configured) {
			Log.Error(new Said(
				"cloud: {0} is set, but with no credentials - set {1} and {2} (or AWS_ACCESS_KEY_ID / AWS_SECRET_ACCESS_KEY)"
				, nameof(S3Options.Bucket), "GAMERFARM_S3_ACCESS_KEY_ID", "GAMERFARM_S3_SECRET_ACCESS_KEY"));

			return !_required;
		}

		_options = options;
		_store = new S3Store(options);
		_root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
		_folders = FoldersFromEnvironment();
		_required = Env("GAMERFARM_S3_REQUIRED") is not ("0" or "false" or "no");
		_verbose = Env("GAMERFARM_S3_VERBOSE") is "1" or "true" or "yes";

		if (int.TryParse(Env("GAMERFARM_S3_INTERVAL_SECONDS"), out int seconds) && (seconds >= 5)) {
			_interval = TimeSpan.FromSeconds(seconds);
		}

		Log.Info(new Said("cloud: state in s3://{0} ({1})", _options.Where, string.Join(", ", _folders)));

		if (Env("GAMERFARM_S3_PULL") is not ("0" or "false" or "no")) {
			bool pulled = await PullAsync().ConfigureAwait(false);

			if (!pulled) {
				if (_required) {
					Log.Error(new Said("cloud: couldn't read s3://{0} - {1}", _options.Where, _lastError));
					Log.Info(new Said("cloud: nothing was written to it either. Check the bucket, the credentials and the "
						+ "endpoint, or set GAMERFARM_S3_REQUIRED=0 to start anyway (its state then stays in this container)"));

					return false;
				}

				Log.Warn(new Said("cloud: couldn't read s3://{0} ({1}) - this run keeps its state in the container, and does NOT "
					+ "write to the bucket: doing so could put an empty config over a real one", _options.Where, _lastError));
			}
		} else {
			// Told to trust what's on disk (a warm volume, a restore): that is also permission to write.
			_canWrite = true;
			_canDelete = true;
		}

		if (_canWrite) {
			StartWatching();
			_stop = new CancellationTokenSource();
			_loop = Task.Run(() => LoopAsync(_stop.Token));
		}

		return true;
	}

	/// <summary>The folders the caller wants kept, with config/ always in it: without the settings there is nothing to restore.</summary>
	private static string[] FoldersFromEnvironment() {
		string raw = Env("GAMERFARM_S3_FOLDERS") ?? DefaultFolders;

		List<string> wanted = raw
			.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			.Select(static f => f.Trim('/').ToLowerInvariant())
			.Where(static f => AllowedFolders.Contains(f))
			.ToList();

		if (!wanted.Contains("config")) {
			wanted.Insert(0, "config");
		}

		return [.. wanted];
	}

	// ── pull ─────────────────────────────────────────────────────────────────

	/// <summary>
	/// The bucket into the folder. A file is fetched when it isn't here, when its size differs, or when it was changed
	/// in the bucket since it landed here; a file this copy has newer is left alone and goes up at the first sweep.
	/// </summary>
	private static async Task<bool> PullAsync() {
		if (_store == null) {
			return false;
		}

		for (int attempt = 1; attempt <= 3; attempt++) {
			using CancellationTokenSource ct = new(TimeSpan.FromSeconds(30));

			try {
				return await PullOnceAsync(ct.Token).ConfigureAwait(false);
			} catch (Exception e) when (attempt < 3) {
				_lastError = Log.Describe(e);
				Log.Warn(new Said("cloud: reading s3://{0} failed ({1}), trying again", _store.Options.Where, _lastError));
				await Task.Delay(TimeSpan.FromSeconds(attempt is 1 ? 1 : 3)).ConfigureAwait(false);
			} catch (Exception e) {
				_lastError = Log.Describe(e);

				return false;
			}
		}

		return false;
	}

	private static async Task<bool> PullOnceAsync(CancellationToken ct) {
		if (_store == null) {
			return false;
		}

		string prefix = _store.Options.Prefix.Length > 0 ? _store.Options.Prefix + "/" : "";
		List<S3Entry> objects = await _store.ListAsync(prefix, ct).ConfigureAwait(false);

		bool ok = true;
		int pulled = 0;

		lock (Gate) {
			KnownFiles.Clear();
		}

		foreach (S3Entry entry in objects) {
			if (!TryMapKey(entry.Key, out string rel) || Excluded(rel)) {
				continue;
			}

			string path = LocalPath(rel);
			long size = -1;
			DateTime mtime = DateTime.MinValue;

			try {
				FileInfo info = new(path);

				if (info.Exists) {
					size = info.Length;
					mtime = info.LastWriteTimeUtc;
				}
			} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
				Log.Failed($"cloud: looking at {rel}", e);

				continue;
			}

			// Here, the same size, and not changed in the bucket since: nothing to fetch, and it's already in the list.
			if (size == entry.Size && entry.LastModified <= mtime.AddSeconds(1)) {
				Remember(rel, size, mtime.Ticks, hash: null);

				continue;
			}

			try {
				byte[]? data = await _store.GetAsync(entry.Key, ct).ConfigureAwait(false);

				if (data == null) {
					ok = false;
					_lastError = $"{entry.Key} vanished while it was being read";

					continue;
				}

				string dir = Path.GetDirectoryName(path) ?? _root;

				Directory.CreateDirectory(dir);
				AtomicFile.Write(path, data);
				Remember(rel, data.Length, File.GetLastWriteTimeUtc(path).Ticks, Fingerprint(data));
				pulled++;
				_downloads++;

				if (_verbose) {
					Log.Debug($"cloud: pulled {rel} ({data.Length} bytes)");
				}
			} catch (Exception e) when (e is S3Exception or IOException or UnauthorizedAccessException) {
				ok = false;
				_lastError = $"{entry.Key}: {Log.Describe(e)}";
				Log.Warn(new Said("cloud: couldn't read {0} from the bucket ({1})", entry.Key, Log.Describe(e)));
			}
		}

		_canWrite = true;
		_canDelete = ok;

		lock (Gate) {
			_files = KnownFiles.Count;
		}

		_lastOk = DateTime.UtcNow;

		if (pulled > 0) {
			Log.Info(new Said("cloud: {0} file(s) restored from s3://{1}", pulled, _store.Options.Where));
		}

		return true;
	}

	private static string LocalPath(string rel) => Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar));

	/// <summary>"gamerfarm/config/x.json" → "config/x.json", when it's under one of the synced folders. Anything else in
	/// the bucket (another farm's prefix, a stray object) is left alone.</summary>
	private static bool TryMapKey(string key, out string rel) {
		rel = "";

		string body = _options is { Prefix.Length: > 0 } o
			? (key.StartsWith(o.Prefix + "/", StringComparison.Ordinal) ? key[(o.Prefix.Length + 1)..] : "")
			: key;

		if (body.Length == 0) {
			return false;
		}

		int slash = body.IndexOf('/');

		if ((slash <= 0) || !_folders.Contains(body[..slash])) {
			return false;
		}

		// A key is never a way out of the folder, however it was written into the bucket.
		if (body.Split('/').Any(static part => part is "" or "." or "..")) {
			return false;
		}

		rel = body;

		return true;
	}

	// ── watching, sweeping, stopping ─────────────────────────────────────────

	private static void StartWatching() {
		foreach (string folder in _folders) {
			string dir = Path.Combine(_root, folder);

			try {
				Directory.CreateDirectory(dir);

				FileSystemWatcher watcher = new(dir) {
					IncludeSubdirectories = true,
					NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
					InternalBufferSize = 64 * 1024
				};

				watcher.Changed += Woke;
				watcher.Created += Woke;
				watcher.Deleted += Woke;
				watcher.Renamed += Woke;
				watcher.Error += (_, e) => Log.Debug($"cloud: watcher on {folder}: {e.GetException().Message}");
				watcher.EnableRaisingEvents = true;
				Watchers.Add(watcher);
			} catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) {
				// The 30-second sweep still catches everything; a watcher is a shortcut, not the mechanism.
				Log.Debug($"cloud: can't watch {folder} ({Log.Describe(e)}) - the sweep covers it");
			}
		}
	}

	private static void Woke(object sender, FileSystemEventArgs e) {
		try {
			if (Wake.CurrentCount < 1000) {
				Wake.Release();
			}
		} catch (SemaphoreFullException) {
			// a burst bigger than the count allows: one wake is all it takes
		}
	}

	private static async Task LoopAsync(CancellationToken ct) {
		try {
			while (!ct.IsCancellationRequested) {
				bool woken = await Wake.WaitAsync(_interval, ct).ConfigureAwait(false);

				if (woken) {
					while (Wake.Wait(0)) {
						// drain a burst
					}

					// A save is a write and a rename; let both land before the folder is read.
					await Task.Delay(Debounce, ct).ConfigureAwait(false);
				}

				await SweepAsync(final: false, ct).ConfigureAwait(false);
			}
		} catch (OperationCanceledException) {
			// stopping
		} catch (Exception e) {
			Log.Failed("cloud: the sync loop stopped", e);
		}
	}

	/// <summary>
	/// One pass over the folders: every file that changed since the last pass is put in the bucket, and every file that
	/// was in it and isn't here any more is taken out.
	/// </summary>
	private static async Task SweepAsync(bool final, CancellationToken ct) {
		if ((_store == null) || !_canWrite) {
			return;
		}

		bool entered;

		try {
			if (final) {
				await One.WaitAsync(ct).ConfigureAwait(false);
				entered = true;
			} else {
				entered = await One.WaitAsync(TimeSpan.Zero, ct).ConfigureAwait(false);
			}
		} catch (OperationCanceledException) {
			return;
		}

		if (!entered) {
			return;
		}

		try {
			HashSet<string> present = new(StringComparer.Ordinal);

			foreach (string folder in _folders) {
				string dir = Path.Combine(_root, folder);

				if (!Directory.Exists(dir)) {
					continue;
				}

				IEnumerable<string> files;

				try {
					files = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories);
				} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
					Log.Failed($"cloud: reading {folder}", e);

					continue;
				}

				foreach (string file in files) {
					if (ct.IsCancellationRequested) {
						return;
					}

					string rel = Path.GetRelativePath(_root, file).Replace(Path.DirectorySeparatorChar, '/');

					if (Excluded(rel)) {
						continue;
					}

					present.Add(rel);

					try {
						await ConsiderAsync(rel, file, ct).ConfigureAwait(false);
					} catch (Exception e) when (e is S3Exception or IOException or UnauthorizedAccessException) {
						_lastError = $"{rel}: {Log.Describe(e)}";
						Log.Warn(new Said("cloud: couldn't save {0} to the bucket ({1})", rel, Log.Describe(e)));
					}
				}
			}

			await DeleteGoneAsync(present, ct).ConfigureAwait(false);

			lock (Gate) {
				_files = KnownFiles.Count;
			}

			_lastOk = DateTime.UtcNow;
		} finally {
			One.Release();
		}
	}

	private static async Task ConsiderAsync(string rel, string path, CancellationToken ct) {
		if (_store == null) {
			return;
		}

		FileInfo info = new(path);

		if (!info.Exists) {
			return;   // gone between the listing and now: the delete pass has it
		}

		long size = info.Length;
		long ticks = info.LastWriteTimeUtc.Ticks;

		Known known;

		lock (Gate) {
			KnownFiles.TryGetValue(rel, out known);
		}

		// Nothing moved since the last look - the common case, and it costs one stat.
		if ((known.Size == size) && (known.Ticks == ticks) && (known.Hash != null)) {
			return;
		}

		byte[] data = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
		string hash = Fingerprint(data);

		if (known.Hash == hash) {
			Remember(rel, size, ticks, hash);

			return;
		}

		await _store.PutAsync(_store.Key(rel), data, ct).ConfigureAwait(false);
		Remember(rel, size, ticks, hash);
		_uploads++;

		if (_verbose) {
			Log.Debug($"cloud: saved {rel} ({size} bytes)");
		}
	}

	/// <summary>
	/// Files that were put in the bucket and aren't here any more: an account removed, a state file the app cleaned up.
	/// A whole folder going at once is never taken at face value - that is a mount that didn't come up, not the user
	/// deleting forty files - and is left for 'cloudsync now' to do on purpose.
	/// </summary>
	private static async Task DeleteGoneAsync(HashSet<string> present, CancellationToken ct) {
		if (_store == null) {
			return;
		}

		List<string> gone;

		lock (Gate) {
			gone = KnownFiles.Keys.Where(rel => !present.Contains(rel)).ToList();
		}

		int friends;

		lock (Gate) {
			friends = KnownFiles.Count;
		}

		if (gone.Count == 0) {
			return;
		}

		if (!_canDelete) {
			Log.Debug($"cloud: {gone.Count} file(s) are gone here but were never fully read back - not deleting them from the bucket");

			return;
		}

		if ((gone.Count > 40) && (gone.Count * 2 > friends)) {
			Log.Warn(new Said("cloud: {0} of {1} files vanished at once - leaving them in the bucket; 'cloudsync now' removes them if that was meant",
				gone.Count, friends));

			return;
		}

		foreach (string rel in gone) {
			if (ct.IsCancellationRequested) {
				return;
			}

			try {
				await _store.DeleteAsync(_store.Key(rel), ct).ConfigureAwait(false);
				_deletes++;

				lock (Gate) {
					KnownFiles.Remove(rel);
				}

				if (_verbose) {
					Log.Debug($"cloud: removed {rel} from the bucket");
				}
			} catch (Exception e) when (e is S3Exception or IOException) {
				_lastError = $"{rel}: {Log.Describe(e)}";
				Log.Warn(new Said("cloud: couldn't remove {0} from the bucket ({1})", rel, Log.Describe(e)));
			}
		}
	}

	/// <summary>
	/// The last sweep, inside the shutdown: whatever a clean exit wrote (lifetime totals, the last settings save) goes
	/// to the bucket before the process is gone. Bounded - a stop that hangs on a dead network is worse than a lost file.
	/// </summary>
	public static async Task StopAsync() {
		if (_store == null) {
			return;
		}

		try {
			_stop?.Cancel();
		} catch (ObjectDisposedException) {
			// already stopped
		}

		try {
			if (_loop != null) {
				await _loop.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
			}
		} catch (Exception e) when (e is TimeoutException or OperationCanceledException) {
			// still running a sweep: the final one below waits for it
		}

		foreach (FileSystemWatcher watcher in Watchers) {
			try {
				watcher.EnableRaisingEvents = false;
				watcher.Dispose();
			} catch (Exception) {
				// disposing a watcher can't fail in a way that matters here
			}
		}

		Watchers.Clear();

		if (!_canWrite) {
			_store.Dispose();
			_store = null;

			return;
		}

		int before = _uploads;

		try {
			using CancellationTokenSource ct = new(TimeSpan.FromSeconds(20));
			await SweepAsync(final: true, ct.Token).ConfigureAwait(false);
		} catch (Exception e) {
			Log.Warn(new Said("cloud: the last save to s3://{0} didn't finish ({1})", _store.Options.Where, Log.Describe(e)));
		}

		if (_uploads > before) {
			Log.Info(new Said("cloud: {0} file(s) saved to s3://{1}", _uploads - before, _store.Options.Where));
		}

		_store.Dispose();
		_store = null;
	}

	// ── what the console and the log say ─────────────────────────────────────

	/// <summary>One line at startup, after the log file is open, so the folder it came from is written down with the rest.</summary>
	public static void Announce() {
		if ((_store == null) && (_options == null)) {
			return;
		}

		if (_options == null) {
			return;
		}

		if (_canWrite) {
			Log.Info(new Said("cloud: s3://{0} is the state; every change goes back there ({1} file(s) here)", _options.Where, _files));
		} else {
			Log.Warn(new Said("cloud: not writing to s3://{0} this run - it couldn't be read at start, and putting this "
				+ "container's state over it could lose the real one", _options.Where));
		}
	}

	/// <summary>'cloudsync' - where the state is, how it's going, and 'now' to push everything this second.</summary>
	public static async Task<string> CommandAsync(string[] rest) {
		if (_store == null) {
			if (_options == null) {
				return "S3 is off: set GAMERFARM_S3_BUCKET (and its credentials) and restart - see docs/docker-s3.md.";
			}

			return $"S3 is configured for {_options.Where}, but this run isn't syncing (the bucket couldn't be read at start).";
		}

		if (!_canWrite) {
			return $"cloud: s3://{Where} couldn't be read at start, so nothing is being written to it this run.{Environment.NewLine}"
				+ Status();
		}

		if (rest.Any(static w => w is "now" or "sync" or "flush")) {
			int before = _uploads;

			using CancellationTokenSource ct = new(TimeSpan.FromSeconds(60));
			await SweepAsync(final: true, ct.Token).ConfigureAwait(false);

			return $"cloud: {_uploads - before} file(s) saved to s3://{Where} just now.{Environment.NewLine}{Status()}";
		}

		return Status();
	}

	public static string Status() {
		if (_options == null) {
			return "S3 is off: the state stays in this container (set GAMERFARM_S3_BUCKET to keep it in a bucket).";
		}

		StringBuilder sb = new();

		sb.Append("cloud: s3://").Append(_options.Where).Append(" - ").Append(string.Join(", ", _folders)).AppendLine();
		sb.Append("  ").Append(_files).Append(" file(s) here, ").Append(_uploads).Append(" saved, ")
			.Append(_downloads).Append(" restored, ").Append(_deletes).Append(" removed this run").AppendLine();

		sb.Append(_canWrite
			? $"  watching for changes, and a sweep every {(int) _interval.TotalSeconds}s"
			: "  NOT writing to the bucket this run (it couldn't be read at start)");

		if (_lastOk != DateTime.MinValue) {
			sb.Append("; last round ").Append(_lastOk.ToLocalTime().ToString("HH:mm:ss"));
		}

		if (_lastError.Length > 0) {
			sb.AppendLine().Append("  last problem: ").Append(_lastError);
		}

		return sb.ToString().TrimEnd();
	}

	// ── small pieces ─────────────────────────────────────────────────────────

	/// <summary>Files that are never the state: the lock of the copy running now, and half-written temporary copies.</summary>
	private static bool Excluded(string rel) =>
		NeverPaths.Contains(rel)
		|| rel.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
		|| rel.EndsWith("/instance.lock", StringComparison.OrdinalIgnoreCase)
		|| Path.GetFileName(rel).StartsWith('.')
		|| rel.Split('/').Any(static part => part.StartsWith('.'));

	private static string Fingerprint(byte[] data) => Convert.ToHexString(MD5.HashData(data)).ToLowerInvariant();

	private static void Remember(string rel, long size, long ticks, string? hash) {
		lock (Gate) {
			KnownFiles[rel] = new Known(size, ticks, hash);
		}
	}

	private static string? Env(string name) {
		string? value = Environment.GetEnvironmentVariable(name);

		return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
	}
}