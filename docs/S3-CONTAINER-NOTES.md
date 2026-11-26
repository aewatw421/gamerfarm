# 无盘容器改造：经验与上游同步手册

> 这份文件是**给未来的我（或任何接手的 agent）**看的操作手册，不是给最终用户的文档。
> 面向用户的说明在 [docker-s3.md](docker-s3.md)。
>
> 触发场景：用户说「同步 VisaHolder/gamerfarm 的更新」「上游出新版本了，合并一下」。
> 目标：把上游新代码合进来，**同时不丢失**下面的改造，并且用同样的验证手段确认没坏。

---

## 1. 这次到底改了什么（一句话版）

让 gamer.farm 的整个数据目录（`config/`、`plugins/`）可以住在 S3 兼容桶里，
于是容器**不需要任何持久卷**：启动时从桶拉取、运行中持续回传、停机时最后落盘。

于是 Dockerfile 里的 `VOLUME` 被删掉了，镜像可以在 Render / Cloud Run / Fargate 这类
「临时文件系统」平台上直接跑，重建容器后账号不用重新登 Steam。

---

## 2. 改造清单（17 个文件：9 新增 + 8 修改）

同步后必须逐项确认这些还在。**新增文件**（上游不会有，冲突=0，最容易保住）：

| 文件 | 作用 | 丢了会怎样 |
|---|---|---|
| `src/GamerFarm/Core/S3Store.cs` | 手写 SigV4 客户端（GET/PUT/DELETE/ListObjectsV2） | 完全不工作 |
| `src/GamerFarm/Core/CloudSync.cs` | 拉取/监听/扫描/删除保护/停机落盘 | 完全不工作 |
| `tests/s3-mock.mjs` | 会**校验签名**的假桶（启动前用 AWS 官方示例自检） | CI 第一道测试失效 |
| `tests/s3-state-check.sh` | 真二进制 + 假桶，走「改设置→SIGTERM→删目录→重启」 | CI 第一道测试失效 |
| `tests/docker-s3-smoke.sh` | 真镜像 + 真 MinIO，断言 `.Mounts==0` | CI 第二道测试失效 |
| `tests/s3-make-bucket.mjs` | 用我们自己的签名器建桶 | CI 建桶步骤失效 |
| `docs/docker-s3.md` | 用户文档：变量表、各云厂、故障对照 | 用户看不懂 |
| `docker-compose.s3.example.yml` | 零挂载示例 | 少一个入口 |
| `.github/workflows/docker-image.yml` | 构建 + 双测试 + 推 ghcr.io | 没有镜像 |
| `docs/S3-CONTAINER-NOTES.md` | 本文件（同步手册） | 下次同步从零猜 |

**修改文件**（会冲突，重点看）：

| 文件 | 改动 | 冲突风险 |
|---|---|---|
| `src/GamerFarm/Program.cs` | 4 处调用 | **中高**，上游常改启动流程 |
| `src/GamerFarm/Commands.cs` | 命令表 1 行 + dispatch 1 行 | **中**，上游常加命令 |
| `Dockerfile` | 删 `VOLUME`，加注释 | **中**，上游可能改回 |
| `docs/COMMANDS.md` | 生成物，`cloudsync` 一行 | 低（重新生成即可） |
| `docs/README.md`、`README.md` | 各加一条链接 | 低 |
| `.gitignore` | 加 `core` / `core.*` | 低 |

---

## 3. 那 6 个代码锚点（同步时按行号找会错，按内容找）

同步上游后，用这些**特征字符串**重新定位，不要信行号：

### 3.1 Program.cs — 拉取（必须在任何东西读配置之前）

锚点：`ConfigStore.UseRoot(root);` 这一行**之前**。我们插入的块：

```csharp
if (!ping && !steamSelfTest && !quitRunning && (setupChoices == null)) {
    if (!await GamerFarm.Core.CloudSync.StartAsync(root).ConfigureAwait(false)) {
        return 1;
    }
}
```

**为什么必须在这**：`ConfigStore.UseRoot` → `LoadGlobal` → 各模块读 state，
全部发生在它之后。放晚了就会出现「先读到空配置、再拉取覆盖」的竞态。

**为什么排除那 4 个开关**：`--ping` 是 Docker HEALTHCHECK 用的，它不该触发 S3 拉取（否则健康检查
变成网络请求，桶挂了就判不健康）；`--steam-selftest`、`--quit`、`--setup` 都是不启动 app 的一次性动作。

### 3.2 Program.cs — 日志宣告

锚点：`Log.Configure(global.FileLogging, global.Debug, root, global.LogRetentionDays);` **之后**。
原因：`StartAsync` 跑在 `Log.Configure` 之前，那时写日志还没落地文件，所以要在日志打开后补一句。

### 3.3 Program.cs — 停机落盘

锚点：`BotManager.Flush();` **之后**、`await Task.WhenAny(console, ...)` **之前**。

```csharp
await GamerFarm.Core.CloudSync.StopAsync().ConfigureAwait(false);
```

**顺序是这个改造的灵魂**：`manager.DisposeAsync()` 和 `BotManager.Flush()` 会把
「最后几分钟的累计时长」写到磁盘——这些写入发生在停机流程里，必须**在它们之后**再同步一次，
否则干净退出丢掉的就是最后那一段数据。同时又必须在 1 秒的 console 等待之前，免得被时间窗吃掉。

### 3.4 Commands.cs — 命令表

锚点：`new("backup", ...)` 那一行**后面**加一条 `new("cloudsync", ...)`。
注意 `GroupSettings`，`docs/COMMANDS.md` 生成器按这个分组。

### 3.5 Commands.cs — dispatch

锚点：`"backup" => await BackupCommand(rest).ConfigureAwait(false),` **后面**加：
```csharp
"cloudsync" => await CloudSync.CommandAsync(rest).ConfigureAwait(false),
```

### 3.6 Dockerfile — 删除 VOLUME

**这是最容易被上游改回去的一处。** 上游的 `VOLUME ["/data/config", "/data/logs", "/data/backups", "/data/plugins"]`
如果回来了，容器会创建匿名卷，在 Render 上表现为「每次部署数据看似还在但其实是新卷」，
或者更糟——测试里 `.Mounts == 0` 断言直接失败。

同步后必须 `grep -n VOLUME Dockerfile` 确认没有输出。

---

## 4. 设计里那些「不显然但不能动」的决定

同步时如果看到某行代码觉得多余，先读这一节。

### 4.1 拉取失败就不写（最重要）

`GAMERFARM_S3_REQUIRED` 默认 `1`：桶读不到 → 直接 `return 1` 退出。
`=0` 时启动，但那一次运行 `_canWrite = false`，**一个字节都不往桶里写**。

**理由**：如果拉取失败还继续跑，app 会用默认配置覆盖掉桶里的真实配置，
下一步第一次保存就把整个 farm 设置清零。这是灾难性数据丢失，宁可启动失败。

真实 B2 实测过：给错 secret + `REQUIRED=0`，桶里 0 个对象，日志明确
`NOT writing to the bucket this run`。

### 4.2 `instance.lock` 永不同步

`NeverPaths = ["config/state/instance.lock"]`。

**理由**：这是单实例锁，属于「当前正在跑的那个进程」。如果同步了，
下次启动会拉回一个僵尸锁，表现为「说已经在运行、然后退出」。

另外 `.tmp` 后缀和 `.` 开头的文件也排除（原子写的临时文件、隐藏文件）。

### 4.3 整目录消失不删桶（删除保护）

`DeleteGoneAsync` 里：`gone.Count > 40 && gone.Count * 2 > friends` → 拒绝删除并 WARN。

**理由**：挂载点没挂上 / 卷没起来，在文件系统看来和「用户删光了所有文件」一模一样。
直接照着删就把桶清空了。宁可留着让用户手动 `cloudsync now` 确认。

### 4.4 拉取没完全成功就不删

`_canDelete` 只在 `PullOnceAsync` 全部成功时才 `true`。

**理由**：如果某个对象读失败，本地就没有它，删除逻辑会认为「本地没有=桶里该删」，
把没读成功的对象删掉。读不全 → 不删。

### 4.5 比较用 size + mtime + MD5

`Known(Size, Ticks, Hash)`：
- 快速路径：size 和 mtime 都没变，且已有 Hash → 跳过（一次 `stat`，不读文件）
- 慢路径：读了文件算 MD5，和上次上传的比，**一样就不传**

**理由**：很多模块会周期性重写内容没变的 state 文件（比如每 30 秒刷一次状态），
只比 mtime 会造成无意义的持续上传。B2 上 23 个对象的情况下这个优化很关键。

### 4.6 先写临时文件再改名

复用项目已有的 `AtomicFile.Write`。

**理由**：拉取时如果直接写目标文件，中途失败就留下半个 JSON，下次启动解析失败。
项目里 `AtomicFile` 本来就是为这个写的，直接复用而不是自己 `File.WriteAllBytes`。

### 4.7 `--ping` 不触发 S3

已在 3.1 说明。**这条特别容易在上游改动时被破坏**——如果哪天 HEALTHCHECK 改成了别的入口，
或者有人「顺手」把 CloudSync 调用提前到参数解析之前，健康检查就会依赖网络。

### 4.8 路径穿越防护

`TryMapKey`：拒绝空段、`.`、`..`；`body.Split('/').Any(part is "" or "." or "..")`。
只接受 `_folders` 白名单里的顶层目录（`config`/`plugins`/`backups`/`logs`）。

**理由**：桶里的 key 是外部数据，可以被人写。`../../etc/passwd` 这种 key 必须挡住。

### 4.9 SigV4 的 canonical request 少一个换行 = 全盘失败

这个坑真实踩过，**CI 抓出来的**：

```
canonical = METHOD \n PATH \n QUERY \n
            host:...\n x-amz-content-sha256:...\n x-amz-date:...\n
            \n                          ← 这个空行！
            signedHeaders \n payloadHash
```

`canonical headers block` 自己以 `\n` 结尾，格式里**还要再跟一个** `\n` 才是空行。
少这一个字节 → 所有请求 `SignatureDoesNotMatch`，而且**只在真实桶上才暴露**，
假桶如果也写错就互相抵消了。

所以 `tests/s3-mock.mjs` 启动第一件事是拿 **AWS 官方发布的示例签名**自检：
```
examplebucket / test.txt / GET / 20130524T000000Z
→ 必须是 f0e8bdb87c964420e857bd35b5d6ed310bd44f0170aba48dd91039c6036bdb41
```
对不上就 `process.exit(1)`，拒绝服务。**这段绝对不能删**——它是唯一能挡住签名实现悄悄写错的东西。

同样地，`tests/s3-make-bucket.mjs` 里也有这个坑：`SignedHeaders` 列了 3 个 header，
`canonical headers` 里就必须写全 3 个。第一次 CI 失败就是这里
（当时只写了 `host:`，MinIO 直接 403）。

---

## 5. 验证手段（同步后按顺序跑）

### 5.1 编译 + 单测

```bash
export DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1   # 这台机器没 ICU，不加会 core dump
dotnet build src/GamerFarm/GamerFarm.csproj -c Release -v q
cd tests/GamerFarm.Tests && dotnet run -c Release -m:1
```

**注意**：单测全量会在这里因 `CultureNotFoundException: de-DE` 崩掉。
这是**本机环境问题**（`DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1` 禁掉了真实 culture），
不是代码问题。判断标准：崩之前那些 `PASS` 里，
「update check / docs: COMMANDS.md / help:」这几组是我改动会影响的，
它们 PASS 就够了。要跑全量得装 `libicu`。

### 5.2 无 Docker 的端到端（必须先于 Docker）

```bash
sh tests/s3-state-check.sh
```
期望结尾：`PASS: the state lived in the bucket, not in the data folder`。
覆盖：假桶签名校验 → 上传 → 改设置 → SIGTERM → 删 `/data` → 重启 → 设置和 AES key 都回来。

### 5.3 生成文档（改动了 Commands.cs 就必须）

上游用 `python tools/gen-commands.py`，**这台机器没有 python**。
我写了一个 Node 版等价物，行为已和原版逐字节对齐（只差 `cloudsync` 那行）：

```bash
node /tmp/gen-commands.mjs       # 生成 /tmp/commands.new.md
diff /workspace/gamerfarm/docs/COMMANDS.md /tmp/commands.new.md
cp /tmp/commands.new.md /workspace/gamerfarm/docs/COMMANDS.md
```

**如果 `/tmp` 已清空**，照 `tools/gen-commands.py` 重写一份即可：
正则 `new\(\s*"((?:[^"\\]|\\.)*)",\s*"..."`，从 `IReadOnlyList<CommandDef> All = [` 到 `\n\t];`，
按 `Group\w+` 常量映射分组，行尾 `| \`cmd args\` <br>also \`alias\` | help |`。
判断是否成功：只应该出现「`cloudsync` 那一行 + 命令总数 +1」的差异，多一处都是写错了。

### 5.4 真实 B2（最终验收）

凭据见第 8 节。流程：
1. 空 `/data` 启动 → 应 `N file(s) restored`（桶里已有的）
2. 改一个设置 → `cloudsync now` → 桶里对象 mtime 更新
3. SIGTERM → 日志有 `cloud: N file(s) saved to s3://...`
4. `rm -rf /data` → 重启 → 设置 + `secret.key` md5 **完全相同** + `plugins/` 文件回来
5. 收尾：把测试前缀的对象全删掉（用 5.6 的脚本）

### 5.5 推 ghcr.io

`ghcr.io/aewatw421/gamerfarm`，公开包。workflow 是
`build-and-test`（无 Docker 测试 → 建桶 → 真镜像 smoke）→ `publish`（amd64+arm64）。

手动触发：
```bash
curl -X POST -H "Authorization: token $GITHUB_TOKEN" \
  https://api.github.com/repos/aewatw421/gamerfarm/actions/workflows/docker-image.yml/dispatches \
  -d '{"ref":"main"}'
```

### 5.6 往桶里看 / 清理

`/tmp` 里的脚本会被清掉，需要时照这段重写（**这是唯一正确的 SigV4 写法，含那个空行**）：

```javascript
// 关键三行，其余照 S3Store.cs 抄
const cr = [method, url.pathname, q,
  `host:${url.host}\nx-amz-content-sha256:${ph}\nx-amz-date:${amzDate}\n`,
  'host;x-amz-content-sha256;x-amz-date', ph].join('\n');
```
`q` 必须按 name 排序且 URL 编码；DELETE 时 key 的每一段单独 `encodeURIComponent` 再 join `/`，
不要整条编码（`%2F` 会被 B2 当字面量）。

---

## 6. 网络与环境的坑（都是实测出来的）

### 6.1 Git 推送：https 到 github.com 经常 TLS 失败

**症状**：`GnuTLS recv error (-110)` / `gnutls_handshake() failed` / `error in the HTTP2 framing layer`。
注意 `api.github.com` **是通的**，只有 `github.com` 的 git 通道不稳。

**处理顺序**：
1. 先直连试（有时能成）
2. 挂代理：`env https_proxy=http://192.168.1.13:7890 http_proxy=http://192.168.1.13:7890 git ...`（**常常有效**）
3. 加 `-c http.version=HTTP/1.1`（HTTP/2 framing 错误时有用）
4. 都不行 → **改走 contents API 逐文件提交**（`api.github.com` 一直通）：
   ```bash
   sha=$(curl -s -H "Authorization: token $TOKEN" \
     "https://api.github.com/repos/OWNER/REPO/contents/PATH?ref=main" | node -pe 'JSON.parse(require("fs").readFileSync(0)).sha')
   node -e 'const fs=require("fs");fs.writeFileSync("/tmp/p.json",JSON.stringify({
     message:"...", content:fs.readFileSync("PATH").toString("base64"), sha:process.argv[1], branch:"main"}))' "$sha"
   curl -X PUT -H "Authorization: token $TOKEN" -H "Accept: application/vnd.github+json" \
     "https://api.github.com/repos/OWNER/REPO/contents/PATH" -d @/tmp/p.json
   ```

### 6.2 推送被拒：805MB 的 core dump

**症状**：`remote: error: File tests/GamerFarm.Tests/core is 805.63 MB; this exceeds GitHub's file size limit`

**原因**：单测崩溃时 .NET 在 cwd 落了 core dump，被 `git add -A` 收进去了。

**处理**：
```bash
git rm --cached tests/GamerFarm.Tests/core && rm -f tests/GamerFarm.Tests/core
printf '\ncore\ncore.*\n' >> .gitignore
git commit --amend --no-edit     # 如果只在最后一个 commit 里
```
**预防**：跑单测前后都 `ls core`，`.gitignore` 里那两行不要删。

### 6.3 上游仓库是 shallow clone

`git clone --depth 50` 下来的推不上去（`did not receive expected object`）。
```bash
git fetch --unshallow upstream
```
顺带会拉到 tag。

### 6.4 dotnet 环境（本机）

```bash
export PATH=/workspace/.dotnet:$PATH
export HOME=/workspace/.dotnethome DOTNET_CLI_HOME=/workspace/.dotnethome   # 默认 HOME 不可写
export DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1                              # 没 libicu
export DOTNET_NOLOGO=1
```
SDK 在 `/workspace/.dotnet`（10.0.404）。

### 6.5 本机没有 Docker

所以 `docker-s3-smoke.sh` 只能在 CI 里跑。要本地验证镜像内容，
用 registry API 手工解 tar（`/tmp/img-extract.mjs` 的思路）：
取 index → 选 amd64 manifest → 下各 layer blob → gunzip → 走 512 字节 tar 头。
**验证 .NET 字符串要用 UTF-16LE**（字符串在 metadata heap 里），
用 latin1 搜会全部 false negative——这个坑我踩了一次。

### 6.6 长驻进程要用 setsid + nohup

`bash -c "... &"` 在工具调用结束时会被 SIGTERM 带走。要：
```bash
setsid nohup dotnet app.dll --path /tmp/x --no-gui > /tmp/x.log 2>&1 < /dev/null &
```

---

## 7. Render 部署要点

- **端口**：Render 固定转发到 `PORT`（默认 10000）。必须有 `GAMERFARM_WEB_PORT=10000`，
  否则应用听 7242，外部 502。
- **不需要任何 Disk**：这正是改造的意义。`inspect .Mounts == 0`。
- **健康检查路径**填 `/api/ping`：未登录也返回 200，适合做存活探针。
- **SIGTERM**：Render 零停机部署时发 SIGTERM，默认 30 秒后 SIGKILL。
  我们的停机落盘实测约 2 秒，够用。要更长可设 `maxShutdownDelaySeconds`。
- **冷启动**：Free 计划实测 **超过 3 分钟**（首次拉 107MB 镜像 + 0.1 CPU 上跑 .NET + 从 B2 拉 23 对象）。
  之后约 20 秒。不要误判为「挂了」。
- **Free 计划 15 分钟无入站流量就休眠**，Steam 连接会断；唤醒后靠 B2 里的 token 自动重连。
- **日志里的 `api.github.com ... 403 (rate limit exceeded)`**：Render 出口 IP 被 GitHub 匿名限流，
  只是不检查新版本，**不影响运行**。想消掉就设 `GAMERFARM_UPDATE_FEED=`。
- **`Accounts: 16 / Users: 11` 这类数字不是用户自己的账号**，是 gamer.mom 的全站匿名统计。
  自己的账号看 `/api/config` 的 `Bots`。

环境变量（A=a 格式，实测可用）：
```
GAMERFARM_WEB_HOST=0.0.0.0
GAMERFARM_WEB_PORT=10000
GAMERFARM_WEB_PASSWORD=<强密码>
GAMERFARM_S3_BUCKET=<bucket>
GAMERFARM_S3_ENDPOINT=https://s3.<region>.backblazeb2.com
GAMERFARM_S3_REGION=<region>
GAMERFARM_S3_ACCESS_KEY_ID=<keyId>
GAMERFARM_S3_SECRET_ACCESS_KEY=<appKey>
GAMERFARM_S3_PREFIX=gamerfarm
TZ=Asia/Shanghai
```

---

## 8. 测试用真实资源

**B2 桶**（用户提供，实测可用）：
- bucket `git-gamerfarm-render`
- endpoint `https://s3.us-west-004.backblazeb2.com`，region `us-west-004`
- keyID `00440679affe7f50000000001`
- appKey `K004F+Z2DJuvIClf62v9OW4N8JdGC7I` ← **含 `+`，shell 里必须加单引号**

**注意**：这是用户的真实桶。测试用完必须清理自己造的前缀
（`gamerfarm/live/`、`gamerfarm/image-test/`、`gamerfarm/state-check` 等），
不要动 `gamerfarm/` 下属于用户的运行数据（`config/testbot*`、`config/gamerFarm.json`、
`config/tokens/*`、`config/state/secret.key` 这些是生产数据）。

**Render 服务**：`https://gamerfarm-latest.onrender.com`，密码见用户当时给的。
Steam 测试账号：`pwvte99232`，昵称 `testbot`，SteamID `76561198601045996`。

---

## 9. 上游同步的具体步骤

```bash
cd /workspace/gamerfarm

# 1. 加/更新上游远端
git remote add upstream https://github.com/VisaHolder/gamerfarm.git 2>/dev/null
git fetch upstream main            # 若 TLS 失败，见 6.1
git fetch --unshallow upstream     # 若是 shallow（见 6.3）

# 2. 看上游改了什么，先判断风险面
git log --oneline HEAD..upstream/main | head -30
git diff --stat HEAD...upstream/main | tail -20
git diff HEAD...upstream/main -- src/GamerFarm/Program.cs src/GamerFarm/Commands.cs Dockerfile
```

**风险分级**：
- 上游**没碰** `Program.cs` / `Commands.cs` / `Dockerfile` → 直接 `git merge upstream/main`，基本无冲突
- 上游**碰了**这三个 → 逐个手工合，按第 3 节的锚点**按内容找位置**（行号一定变了）

```bash
# 3. 合并
git merge upstream/main
# 冲突时：新增文件（S3Store/CloudSync/tests/docs）不会冲突，
#         只有那 6 个锚点会，按第 3 节重贴

# 4. 重贴锚点后逐条自检
grep -n "CloudSync.StartAsync" src/GamerFarm/Program.cs        # 必须在 UseRoot 之前
grep -n "CloudSync.StopAsync"  src/GamerFarm/Program.cs        # 必须在 BotManager.Flush 之后
grep -n "cloudsync" src/GamerFarm/Commands.cs                  # 表 + dispatch 各一处
grep -n VOLUME Dockerfile                                      # 必须无输出
ls tests/s3-mock.mjs tests/s3-state-check.sh tests/docker-s3-smoke.sh

# 5. 验证（第 5 节）
# 6. 更新文档
# 7. 提交推送（第 6.1 节处理网络）
```

**上游如果加了新命令 / 新设置**：`docs/COMMANDS.md` 必须重新生成（第 5.3 节），
否则 CI 的 `docs: COMMANDS.md is generated from this list` 那条测试会失败。

**上游如果动了数据目录结构**（比如新增 `config/xxx/` 子目录）：
`CloudSync.FoldersFromEnvironment` 的白名单 `AllowedFolders` 可能要跟着改；
如果新增的是数据目录且用户需要持久化，把它加进去，并在 `docs/docker-s3.md` 的变量表更新说明。

**上游如果改了 `Secrets.cs` 的加密方案**（marker 从 `nocat-aes1:` 变别的）：
不影响我们——`secret.key` 只是一个文件，照原样搬运；但要在真实验证里
确认「恢复后 token 仍能解密」，用 5.4 第 4 步的 md5 对比 + 登录日志双重确认。

### 9.1 ⚠ 最隐蔽的一类冲突：上游的**测试**会断言 Dockerfile 里有 `VOLUME`

**这件事 2.0.5 同步时真实发生过，必须专门查。**

代码零冲突不代表没冲突。上游 `tests/GamerFarm.Tests/Program.cs` 里有一组对
**Dockerfile 和 docker-compose.example.yml 的静态断言**（搜 `"docker: ` 就能找到，
2.0.5 时大约在第 7823 行「`// Docker: backups kept outside the container...`」那段）。
其中原来有一条是：

```csharp
Check("docker: ... /data/backups and /data/plugins are volumes of the image",
    compose.Contains("- ./backups:/data/backups", StringComparison.Ordinal)
    && dockerfile.Contains("\"/data/backups\", \"/data/plugins\"]", StringComparison.Ordinal)   // ← 断言必须有 VOLUME
    && compose.Contains("\n      - ./plugins:/data/plugins", StringComparison.Ordinal)
    && dockerfile.Contains("mkdir -p /data/config /data/logs /data/backups", StringComparison.Ordinal));
```

我们**故意删掉了 `VOLUME`**，于是这条测试在上游合并后立刻 FAIL：

```
FAIL  docker: ./backups and ./plugins are mounted, and /data/backups and /data/plugins are volumes of the image
```

**为什么不能顺着它改回去**：`VOLUME` 会让 Docker 为每个容器凭空创建匿名卷，
在「无盘容器 + 状态在桶里」的部署上，这是最坏的默认——用户以为没挂盘，
实际上每次重构都造一个新卷、旧卷泄漏在宿主机，而且 `docker-s3-smoke.sh` 里
`.Mounts == 0` 的断言会直接失败。

**正确做法**：把断言**反过来**，同时保留它原本要保护的东西
（compose 里照样挂载、后端目录照样创建），并把理由写在测试旁边：

```csharp
Check("docker: ./backups and ./plugins are mounted, and the image needs no VOLUME of its own",
    compose.Contains("- ./backups:/data/backups", StringComparison.Ordinal)
    && compose.Contains("\n      - ./plugins:/data/plugins", StringComparison.Ordinal)
    && dockerfile.Contains("mkdir -p /data/config /data/logs /data/backups", StringComparison.Ordinal)
    && !dockerfile.Contains("\nVOLUME", StringComparison.Ordinal));   // ← 主张反过来了
```

`"\nVOLUME"` 带前导换行是有意的：它只匹配行首的指令，不会误伤
Dockerfile 里那句解释性的注释「Deliberately NO VOLUME here...」。

**所以同步流程里要加一步**（放在第 9 节第 4 步之后、第 5 步之前）：

```bash
# 上游是否改了对 Dockerfile 的静态断言？
git diff 07e491e upstream/main -- tests/GamerFarm.Tests/Program.cs | grep -nE "VOLUME|volumes of the image|dockerfile\." 
grep -n "needs no VOLUME of its own" tests/GamerFarm.Tests/Program.cs    # 我们的版本还在吗
grep -nE '^\s*VOLUME' Dockerfile                                          # 必须无输出
```

**同类风险**：上游还可能加别的 Dockerfile 断言（比如断言 `EXPOSE 7242`、
断言某个 `ENV`、断言 `HEALTHCHECK` 的具体写法）。我们的 Dockerfile 改动很克制
（只删了 `VOLUME`，其余原样保留），所以只有「断言 VOLUME 存在」这一条会撞上；
但每次同步都该 `git diff` 一下那个测试块，看有没有新增对 Dockerfile 的断言。

运行单测时，判断这条是否回归的方法很简单：
`PASS  docker:` 那 5 条（backups/plugins、time zone、public address、health check、update message）
**必须全 PASS**。这 5 条不依赖网络，是本机能稳定跑出来的；其余 FAIL 见 5.1。

---

## 9.2 同步实录：2.0.3 → 2.0.5（2026-11-26）

记下来当参照，下次能快速判断「这次算顺利还是算麻烦」。

| 项目 | 结果 |
|---|---|
| 上游新增 commit | 2 个（`5abb423` 2.0.4、`bf030bc` 2.0.5） |
| 变更规模 | 18 个文件，+498/−88 |
| 改了什么 | `Modules/SaleEvents.cs`（Steam 2026 颁奖提名，+321）、10 个语言包、`addons/addons.json(.sig)`、`Config/Settings.cs` 一行、`GamerFarm.csproj` 版本号、`tests/Program.cs` +137、2 个 HTML 样本 |
| 我们那 8 个文件被碰了吗 | **一个都没碰**（`Program.cs`/`Commands.cs`/`Dockerfile`/`COMMANDS.md`/`README.md`/`docs/README.md`/`.gitignore`） |
| 合并冲突 | **0** |
| 6 个锚点 | 全部完好，无需重贴 |
| `COMMANDS.md` | 重新生成后 **unchanged**（还是 89 条） |
| 唯一需要动的地方 | 第 9.1 节那条 `VOLUME` 测试断言 |
| 编译 | 0 warning / 0 error |
| `tests/s3-state-check.sh` | PASS |
| CI | run `45750129089` 全绿 |
| 镜像 | `latest` = `sha-8c2cbef`，`.Volumes == null`，DLL 内有 2.0.5 + 全部 S3 符号 |

**这次的经验**：上游碰 `Program.cs` / `Commands.cs` / `Dockerfile` 的时候才需要小心地按锚点重贴；
碰 **测试里的 Dockerfile 断言** 反而更容易漏，因为它不产生 merge 冲突，只在跑单测时冒出来。

---

## 10. 这次交付的验收记录（可以作为「好的样子」参照）

**CI**：run `45318233071`，`build-and-test` 9 步全绿 + `publish` 全绿。

**真实 B2 实测**（两套程序都跑过：本地构建的 dll，以及从 ghcr 镜像里解包的 dll）：

| 项目 | 结果 |
|---|---|
| SigV4 一次通过 B2 校验 | ✅ |
| 空目录 → 上传 | ✅ |
| `cloudsync now` 立即落盘 | ✅ |
| SIGTERM 干净退出 + 最后落盘 | ✅ `cloud: 3 file(s) saved` |
| `rm -rf /data` → 重启 → 恢复 | ✅ `8 file(s) restored` |
| 设置值回来 | ✅ `"UpdateCheckHours": 9` |
| **AES key md5 完全相同** | ✅ 已存登录仍可解密 |
| `plugins/` 文件回来 | ✅ |
| 删除传播（本地删→桶删） | ✅ 11→9 对象 |
| `instance.lock` 未上传 | ✅ |
| `REQUIRED=0` + 错密钥 → 零写入 | ✅ 桶 0 对象，日志明确 |

**Render 真实环境（容器重启后）**：
```
12:16:57  cloud: 23 file(s) restored from s3://git-gamerfarm-render/gamerfarm
12:17:06  testbot  已登录为 pwvte99232（76561198601045996）
12:17:06  testbot  复用 Web 令牌（还可用 23.8 小时）
12:17:08  testbot  本会话正在运行应用 730
```
**恢复 → 登录只隔 9 秒，没有要密码也没有要 Steam Guard 码**，
且 `secret.key`、`tokens/*.token`、`config/*.json` 三者必须互相匹配才能做到——
这是整个改造最关键的一条证据。

---

## 11. 一句话记住

> 数据目录进桶，`Program.cs` 四处、`Commands.cs` 两处、`Dockerfile` 删 `VOLUME`；
> 拉取在 `UseRoot` 之前，落盘在 `Flush` 之后，读不到就不写，锁不同步，签名少一个换行就全崩。
