using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

// 这些命名空间与 TS3AudioBot/TS3Client 来自你的项目引用（和示例插件一致）
using TS3AudioBot;
using TS3AudioBot.CommandSystem;
using TS3AudioBot.Helper;
using TS3AudioBot.Plugins;
using TS3AudioBot.ResourceFactories;
using TS3AudioBot.Audio;
using TS3AudioBot.Config;

using TSLib.Full;          // TsFullClient
using TSLib;               // Ts3Client 等（根据你实际引用）
using Newtonsoft.Json.Linq;

namespace KugouTs3Plugin
{
    public class KugouPlugin : IBotPlugin
    {
        // === 配置 ===
        // 允许在运行时修改；也可改造成从 ini/环境变量读取
        public static string API_Address = "http://localhost:3000";

        // 调用方维度的搜索结果缓存（key 用调用者 UID 或者唯一标识）
        private static readonly Dictionary<string, List<KugouSongItem>> SearchCache = new Dictionary<string, List<KugouSongItem>>();

        // HttpClient 单例
        private static readonly HttpClient http = new HttpClient()
        {
            Timeout = TimeSpan.FromSeconds(15)
        };

        // 依赖注入（与示例插件一致）
        private readonly PlayManager playManager;
        private readonly Ts3Client ts3Client;
        private readonly TsFullClient tsFullClient;

        public KugouPlugin(PlayManager playManager, Ts3Client ts3Client, TsFullClient tsFullClient)
        {
            this.playManager = playManager;
            this.ts3Client = ts3Client;
            this.tsFullClient = tsFullClient;
        }

        public void Dispose()
        {
            // 插件卸载时清理资源（这里用的是静态 HttpClient，不需要释放）
        }

        public void Initialize()
        {
            // 插件加载时的初始化逻辑（可选）
            Console.WriteLine($"[Kugou] Plugin initialized. API_Address = {API_Address}");
        }

        // ============ 命令区 ============

        [Command("kugou search")]
        public async Task<string> CommandSearch(InvokerData invoker, params string[] args)
        {
            string query = string.Join(" ", args ?? Array.Empty<string>()).Trim();
            if (string.IsNullOrWhiteSpace(query))
                return "用法：!kugou search <关键词>";

            try
            {
                var list = await SearchSongsAsync(query);
                // 只取前 10
                var top10 = list.Take(10).ToList();

                // 缓存给当前调用者
                string key = invoker.ClientUid.ToString(); 
                SearchCache[key] = top10;

                // 输出格式
                var sb = new StringBuilder();
                sb.AppendLine("");
                sb.AppendLine("----");
                sb.AppendLine("🔍搜索到的歌曲");
                if (top10.Count == 0)
                {
                    sb.AppendLine("未找到匹配歌曲~");
                }
                else
                {
                    for (int i = 0; i < top10.Count; i++)
                    {
                        var s = top10[i];
                        string display = $"{s.Artist} - {s.Title}";
                        sb.AppendLine($"{i + 1}.【{display}】");
                    }
                    sb.AppendLine("请输入!kugou play 【序号】播放相应歌曲");
                }
                sb.AppendLine("----");

                return sb.ToString();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Kugou] search error: {ex}");
                return "搜索失败：接口错误或网络异常。";
            }
        }

        [Command("kugou play")]
        public async Task<string> CommandPlay(InvokerData invoker, string indexText = null)
        {
            string key = invoker.ClientUid.ToString(); //GetInvokerKey(invoker);
            if (!SearchCache.TryGetValue(key, out var lastList) || lastList == null || lastList.Count == 0)
            {
                return "没有可播放的搜索结果，请先使用 !kugou search <关键词>。";
            }

            int index = 1; // 默认第一首
            if (!string.IsNullOrWhiteSpace(indexText))
            {
                if (!int.TryParse(indexText, out index) || index < 1 || index > lastList.Count)
                {
                    return $"播放失败：无效的序号（1~{(lastList.Count)}）。";
                }
            }

            var song = lastList[index - 1];

            try
            {
                // 根据 hash/albumId 取真实播放 URL（根据你的 API 文档调整）
                string playUrl = await GetSongPlayUrlAsync(song);
                if (string.IsNullOrWhiteSpace(playUrl))
                    return "未获取到播放链接，请尝试其他歌曲或稍后再试。";

                await ts3Client.SendChannelMessage($"🎵 正在播放：{song.Artist} - {song.Title}");
                // 使用 TS3AudioBot 的播放命令（与示例插件一致）
                await MainCommands.CommandPlay(playManager, invoker, playUrl);
                return null; // 已经发过提示，这里返回 null 让框架不重复发消息
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Kugou] play error: {ex}");
                return "播放失败：接口错误或网络异常。";
            }
        }

        [Command("kugou dplay")]
        public async Task<string> CommandDirectPlay(InvokerData invoker, params string[] args)
        {
            string query = string.Join(" ", args ?? Array.Empty<string>()).Trim();
            if (string.IsNullOrWhiteSpace(query))
                return "用法：!kugou dplay <关键词>";

            try
            {
                // 1. 先搜索歌曲
                var list = await SearchSongsAsync(query);
                if (list == null || list.Count == 0)
                {
                    return $"未找到与 '{query}' 相关的歌曲，请尝试其他关键词。";
                }

                // 2. 取第一首歌
                var song = list[0];
                
                // 3. 获取播放链接并播放
                string playUrl = await GetSongPlayUrlAsync(song);
                if (string.IsNullOrWhiteSpace(playUrl))
                    return "未获取到播放链接，请尝试其他关键词或稍后再试。";

                // 4. 缓存搜索结果（方便后续使用 !kugou play 命令）
                string key = invoker.ClientUid.ToString();
                SearchCache[key] = list.Take(10).ToList();

                // 5. 播放歌曲
                await ts3Client.SendChannelMessage($"🎵 直接播放：{song.Artist} - {song.Title}");
                await MainCommands.CommandPlay(playManager, invoker, playUrl);
                return null; // 已经发过提示，这里返回 null 让框架不重复发消息
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Kugou] dplay error: {ex}");
                return "直接播放失败：接口错误或网络异常。";
            }
        }

        [Command("kugou add")]
        public async Task<string> CommandAdd(InvokerData invoker, params string[] args)
        {
            string query = string.Join(" ", args ?? Array.Empty<string>()).Trim();
            if (string.IsNullOrWhiteSpace(query))
                return "用法：!kugou add <关键词>";

            try
            {
                // 1. 先搜索歌曲
                var list = await SearchSongsAsync(query);
                if (list == null || list.Count == 0)
                {
                    return $"未找到与 '{query}' 相关的歌曲，请尝试其他关键词。";
                }

                // 2. 取第一首歌
                var song = list[0];
                
                // 3. 获取播放链接
                string playUrl = await GetSongPlayUrlAsync(song);
                if (string.IsNullOrWhiteSpace(playUrl))
                    return "未获取到播放链接，请尝试其他关键词或稍后再试。";

                // 4. 缓存搜索结果（方便后续使用 !kugou play 命令）
                string key = invoker.ClientUid.ToString();
                SearchCache[key] = list.Take(10).ToList();

                // 5. 添加歌曲到播放队列的下一首位置
                await ts3Client.SendChannelMessage($"➕ 已添加到下一首：{song.Artist} - {song.Title}");
                await MainCommands.CommandAdd(playManager, invoker, playUrl);
                return null; // 已经发过提示，这里返回 null 让框架不重复发消息
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Kugou] add error: {ex}");
                return "添加歌曲失败：接口错误或网络异常。";
            }
        }

        [Command("kugou login")]
        public async Task<string> CommandLogin(InvokerData invoker)
        {
            try
            {
                // 1) 申请登录 key 
                var keyJson = await HttpGetJson($"{API_Address}/login/qr/key?timestamp={GetTimeStamp()}");//获取keyjson
                string loginKey = keyJson["data"]["qrcode"].ToString();//从keyjson获取key
                var createJson = await HttpGetJson($"{API_Address}/login/qr/create?key={Uri.EscapeDataString(loginKey)}&timestamp={GetTimeStamp()}");//获取createjson
                Console.WriteLine($"[Kugou] login key: {loginKey}");
                if (string.IsNullOrEmpty(loginKey))
                    return "登录失败：未获取到二维码 key。";
                

                // 2) 通过key使用api链接 
                var qrApi = "https://api.qrtool.cn/?text=";//二维码生成api
                var loginUrl = createJson["data"]["url"].ToString();//从createjson获取登录url
                var qrCodeUrl =$"[URL]{qrApi}{Uri.EscapeDataString(loginUrl)}[/URL]";//生成可以直接访问的二维码链接
                await ts3Client.SendChannelMessage($"请使用手机酷狗App扫码登录（扫码后请在手机上确认登录）：{qrCodeUrl}");
                
                // 3) 轮询扫码状态
                string token = null;
                const int maxWaitSec = 120;
                var deadline = DateTimeOffset.UtcNow.AddSeconds(maxWaitSec);

                while (DateTimeOffset.UtcNow < deadline)
                {
                    await Task.Delay(1500);
                    var checkRes = await HttpGetJson($"{API_Address}/login/qr/check?key={Uri.EscapeDataString(loginKey)}&timestamp={GetTimeStamp()}");
                    var status = ParseLoginStatus(checkRes);
                    Console.WriteLine($"[Kugou] login status: {status.StatusCode}");
                    // status.StatusCode: 4(成功) / 2(已扫码待确认) / 1(待扫码) 等，具体按你的 API 文档调
                    if (status.StatusCode == 4)
                    {
                        token = status.TokenOrCookie;
                        break;
                    }
                    //else if (status.StatusCode == 2)
                    //{
                        // 已扫码待确认
                        // 可提示用户确认，避免刷屏每 5 次提示一次
                    //}
                    // 其它状态继续等待
                }

                if (string.IsNullOrEmpty(token))
                    return "登录超时或未完成。请重试 !kugou login。";

                // 4) 保存 Token 为 loginToken.txt 到根目录
                string root = AppContext.BaseDirectory; // TS3AudioBot 运行目录
                string filePath = Path.Combine(root, $"loginToken.txt");
                File.WriteAllText(filePath, token ?? string.Empty);

                await ts3Client.SendChannelMessage("🆔登录成功：已保存 token。");
                return null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Kugou] login error: {ex}");
                return "登录失败：接口错误或网络异常。";
            }
        }

        // ============ HTTP & 解析工具 ============

        private static async Task<JObject> HttpGetJson(string url, bool useToken = false)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            // 如需 Referer / UA，可在此加 headers
            // req.Headers.Referrer = new Uri("https://www.kugou.com/");
            
            // 如果需要使用token，则从文件读取并添加到请求头
            if (useToken)
            {
                string token = GetSavedToken();
                if (!string.IsNullOrEmpty(token))
                {
                    req.Headers.Add("Cookie", $"token={token}");
                }
            }
            
            var resp = await http.SendAsync(req);
            resp.EnsureSuccessStatusCode();
            string json = await resp.Content.ReadAsStringAsync();
            return JObject.Parse(json);
        }

        private static async Task<JObject> HttpPostJson(string url, JObject body = null)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Content = new StringContent((body ?? new JObject()).ToString(), Encoding.UTF8, "application/json");
            var resp = await http.SendAsync(req);
            resp.EnsureSuccessStatusCode();
            string json = await resp.Content.ReadAsStringAsync();
            return JObject.Parse(json);
        }

        private static async Task<List<KugouSongItem>> SearchSongsAsync(string keyword)
        {
            // 根据你的 API 文档调整路径/参数：
            // 常见：/search/song?keywords= xxx
            string url = $"{API_Address}/search/song?keywords={Uri.EscapeDataString(keyword)}&pagesize=10&page=1&type=song";
            var jo = await HttpGetJson(url, true); // 使用 token
            return ParseKugouSearchList(jo);
        }

        private static async Task<string> GetSongPlayUrlAsync(KugouSongItem s)
        {
            // 按你的服务网关调整；常见方式是通过 hash/albumId 拿到直链
            // 例如：/song/url?hash=xxx&album_id=yyy
            string url = $"{API_Address}/song/url?hash={Uri.EscapeDataString(s.Hash ?? "")}&album_id={Uri.EscapeDataString(s.AlbumId ?? "")}&free_part=true";
            Console.WriteLine($"[Kugou] GetSongPlayUrlAsync: {url}");
            var jo = await HttpGetJson(url);
            return ParseKugouPlayUrl(jo);
        }

        // ============ 解析区：把你的网关返回 -> 统一的内部结构 ============

        private static List<KugouSongItem> ParseKugouSearchList(JObject jo)
        {
            // 根据实际 JSON 结构解析酷狗搜索结果
            // 数据结构：{ "data": { "lists": [ { ... }, ... ] } }
            var list = new List<KugouSongItem>();

            // 获取搜索结果数组，优先使用 data.lists，兼容其他可能的结构
            JToken arr =
                jo.SelectToken("data.lists") ??
                jo.SelectToken("data.list") ??
                jo.SelectToken("result.songs") ??
                jo.SelectToken("songs") ??
                jo.SelectToken("data.songs");

            if (arr is JArray ja)
            {
                foreach (var it in ja.Take(10)) // 先取多一点，外层再截取前 10
                {
                    // 提取歌曲标题：优先使用 OriSongName，其次 FileName，最后从 FileName 中提取
                    string title = it.Value<string>("OriSongName");
                    if (string.IsNullOrWhiteSpace(title))
                    {
                        string fileName = it.Value<string>("FileName");
                        if (!string.IsNullOrWhiteSpace(fileName))
                        {
                            // FileName 格式通常是 "Artist - Title"，尝试提取标题部分
                            var parts = fileName.Split(new[] { " - " }, 2, StringSplitOptions.None);
                            title = parts.Length > 1 ? parts[1] : fileName;
                        }
                    }

                    // 提取歌手名
                    string artist = it.Value<string>("SingerName");

                    // 提取 Hash，优先级：SQ.Hash > HQ.Hash > FileHash
                    string hash = null;
                    var sqHash = it.SelectToken("SQ.Hash")?.ToString();
                    var hqHash = it.SelectToken("HQ.Hash")?.ToString();
                    var fileHash = it.Value<string>("FileHash");

                    if (!string.IsNullOrWhiteSpace(sqHash))
                        hash = sqHash;
                    else if (!string.IsNullOrWhiteSpace(hqHash))
                        hash = hqHash;
                    else
                        hash = fileHash;

                    // 提取专辑ID
                    string albumId = it.Value<string>("AlbumID");

                    // 检查必要字段
                    if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(artist))
                        continue;

                    list.Add(new KugouSongItem
                    {
                        Title = title?.Trim() ?? "未知标题",
                        Artist = artist?.Trim() ?? "未知歌手",
                        Hash = hash?.Trim(),
                        AlbumId = albumId?.Trim()
                    });
                }
            }

            return list;
        }

        private static string ParseKugouPlayUrl(JObject jo)
        {
            // 根据实际返回的 JSON 结构解析播放 URL
            // 优先级：url 数组 > backupUrl 数组 > 其他兼容字段
            
            // 1. 优先使用 "url" 数组中的第一个地址
            var urlArray = jo.SelectToken("url") as JArray;
            if (urlArray != null && urlArray.Count > 0)
            {
                var firstUrl = urlArray[0]?.ToString();
                if (!string.IsNullOrWhiteSpace(firstUrl))
                    return firstUrl;
            }
            
            // 2. 如果 url 数组为空或无效，尝试 "backupUrl" 数组
            var backupUrlArray = jo.SelectToken("backupUrl") as JArray;
            if (backupUrlArray != null && backupUrlArray.Count > 0)
            {
                var firstBackupUrl = backupUrlArray[0]?.ToString();
                if (!string.IsNullOrWhiteSpace(firstBackupUrl))
                    return firstBackupUrl;
            }
            
            // 3. 兼容旧的 JSON 结构
            var token =
                jo.SelectToken("data.play_url") ??
                jo.SelectToken("data.url") ??
                jo.SelectToken("url") ??
                jo.SelectToken("data.data.play_url");

            return token?.ToString();
        }

        private static string ParseLoginKey(JObject jo)
        {
            // 常见返回：{ "data": { "key": "xxxx" } } 或 { "data": { "unikey": "xxxx" } }
            var t =
                jo.SelectToken("data.key") ??
                jo.SelectToken("data.unikey") ??
                jo.SelectToken("key") ??
                jo.SelectToken("unikey");
            return t?.ToString();
        }

        private static string ParseQrBase64(JObject jo)
        {
            // 常见返回：{ "data": { "qrimg": "data:image/png;base64,..." } } 或 { "data": { "image": "..." } }
            var t =
                jo.SelectToken("data.qrimg") ??
                jo.SelectToken("data.image") ??
                jo.SelectToken("qrimg") ??
                jo.SelectToken("image");
            return t?.ToString();
        }

        private static LoginStatus ParseLoginStatus(JObject jo)
        {
            // 典型：{ "code": 802/803, "data": { "token": "...", "cookie": "..." } }
            var status = new LoginStatus();
            int code = jo["data"]["status"]?.Value<int>() ?? -1;
            status.StatusCode = code;

            // 优先 token，没有则取 cookie
            string token = jo["data"]?["token"]?.ToString();

            status.TokenOrCookie = token;
            return status;
        }

        private static string GetInvokerKey(InvokerData invoker)
        {
            return invoker.ClientUid.ToString();
        }

        private static string GetSavedToken()
        {
            try
            {
                string root = AppContext.BaseDirectory; // TS3AudioBot 运行目录
                string filePath = Path.Combine(root, "loginToken.txt");
                
                if (File.Exists(filePath))
                {
                    return File.ReadAllText(filePath).Trim();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Kugou] Error reading token: {ex}");
            }
            return null;
        }

        private static long GetTimeStamp()
        {
            return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }

        // ============ 内部模型 ============

        private class KugouSongItem
        {
            public string Title { get; set; }
            public string Artist { get; set; }
            public string Hash { get; set; }
            public string AlbumId { get; set; }
        }

        private class LoginStatus
        {
            public int StatusCode { get; set; }
            public string TokenOrCookie { get; set; }
        }
    }
}
