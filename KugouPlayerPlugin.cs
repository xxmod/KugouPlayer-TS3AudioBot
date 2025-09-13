using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
// 这些命名空间与 TS3AudioBot/TS3Client 来自你的项目引用（和示例插件一致）
using TS3AudioBot;
using TS3AudioBot.Audio;
using TS3AudioBot.CommandSystem;
using TS3AudioBot.Config;
using TS3AudioBot.Helper;
using TS3AudioBot.Plugins;
using TS3AudioBot.ResourceFactories;
using TSLib; // Ts3Client 等（根据你实际引用）
using TSLib.Full; // TsFullClient

namespace KugouTs3Plugin
{
    public class KugouPlugin : IBotPlugin
    {
        // === 配置 ===
        // 允许在运行时修改；也可改造成从 ini/环境变量读取
        public static string API_Address = "http://localhost:3000";

        // 调用方维度的搜索结果缓存（key 用调用者 UID 或者唯一标识）
        private static readonly Dictionary<string, List<KugouSongItem>> SearchCache =
            new Dictionary<string, List<KugouSongItem>>();

        // 调用方维度的歌单列表缓存（key 用调用者 UID 或者唯一标识）
        private static readonly Dictionary<string, List<KugouPlaylistItem>> PlaylistCache =
            new Dictionary<string, List<KugouPlaylistItem>>();

        // HttpClient 单例
        private static readonly HttpClient http = new HttpClient()
        {
            Timeout = TimeSpan.FromSeconds(15),
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
                // 优先尝试VIP搜索，失败则使用普通搜索
                var list = await SearchSongsAsync(query, null, true);
                if (list == null || list.Count == 0)
                {
                    list = await SearchSongsAsync(query, null, false);
                }
                
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
            string key = invoker.ClientUid.ToString();
            if (
                !SearchCache.TryGetValue(key, out var lastList)
                || lastList == null
                || lastList.Count == 0
            )
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
                // 优先尝试VIP播放，失败则降级到普通播放
                string playUrl = await GetSongPlayUrlAsync(song, true);
                bool isVipPlay = !string.IsNullOrWhiteSpace(playUrl);
                
                if (string.IsNullOrWhiteSpace(playUrl))
                {
                    playUrl = await GetSongPlayUrlAsync(song, false);
                }
                
                if (string.IsNullOrWhiteSpace(playUrl))
                    return "未获取到播放链接，请尝试其他歌曲或稍后再试。";

                string playMode = isVipPlay ? "👑VIP" : "🎵";
                await ts3Client.SendChannelMessage($"{playMode} 正在播放：{song.Artist} - {song.Title}");
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

        [Command("kugou dplay")] // direct play 直接播放
        public async Task<string> CommandDirectPlay(InvokerData invoker, params string[] args)
        {
            string query = string.Join(" ", args ?? Array.Empty<string>()).Trim();
            if (string.IsNullOrWhiteSpace(query))
                return "用法：!kugou dplay <关键词>";

            try
            {
                // 1. 先搜索歌曲（优先尝试VIP搜索）
                var list = await SearchSongsAsync(query, null, true);
                if (list == null || list.Count == 0)
                {
                    list = await SearchSongsAsync(query, null, false);
                }
                
                if (list == null || list.Count == 0)
                {
                    return $"未找到与 '{query}' 相关的歌曲，请尝试其他关键词。";
                }

                // 2. 取第一首歌
                var song = list[0];

                // 3. 获取播放链接并播放（优先尝试VIP）
                string playUrl = await GetSongPlayUrlAsync(song, true);
                bool isVipPlay = !string.IsNullOrWhiteSpace(playUrl);
                
                if (string.IsNullOrWhiteSpace(playUrl))
                {
                    playUrl = await GetSongPlayUrlAsync(song, false);
                }
                
                if (string.IsNullOrWhiteSpace(playUrl))
                    return "未获取到播放链接，请尝试其他关键词或稍后再试。";

                // 4. 缓存搜索结果（方便后续使用 !kugou play 命令）
                string key = invoker.ClientUid.ToString();
                SearchCache[key] = list.Take(10).ToList();

                // 5. 播放歌曲
                string playMode = isVipPlay ? "👑VIP" : "🎵";
                await ts3Client.SendChannelMessage($"{playMode} 直接播放：{song.Artist} - {song.Title}");
                await MainCommands.CommandPlay(playManager, invoker, playUrl);
                return null;
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
                // 1. 先搜索歌曲（优先尝试VIP搜索）
                var list = await SearchSongsAsync(query, null, true);
                if (list == null || list.Count == 0)
                {
                    list = await SearchSongsAsync(query, null, false);
                }
                
                if (list == null || list.Count == 0)
                {
                    return $"未找到与 '{query}' 相关的歌曲，请尝试其他关键词。";
                }

                // 2. 取第一首歌
                var song = list[0];

                // 3. 获取播放链接（优先尝试VIP）
                string playUrl = await GetSongPlayUrlAsync(song, true);
                bool isVipAdd = !string.IsNullOrWhiteSpace(playUrl);
                
                if (string.IsNullOrWhiteSpace(playUrl))
                {
                    playUrl = await GetSongPlayUrlAsync(song, false);
                }
                
                if (string.IsNullOrWhiteSpace(playUrl))
                    return "未获取到播放链接，请尝试其他关键词或稍后再试。";

                // 4. 缓存搜索结果（方便后续使用 !kugou play 命令）
                string key = invoker.ClientUid.ToString();
                SearchCache[key] = list.Take(10).ToList();

                // 5. 添加歌曲到播放队列的下一首位置
                string addMode = isVipAdd ? "👑VIP" : "➕";
                await ts3Client.SendChannelMessage(
                    $"{addMode} 已添加到下一首：{song.Artist} - {song.Title}"
                );
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
                var keyJson = await HttpGetJson(
                    $"{API_Address}/login/qr/key?timestamp={GetTimeStamp()}"
                ); //获取keyjson
                string loginKey = keyJson["data"]["qrcode"].ToString(); //从keyjson获取key

                // 2) 必需的createJson请求（触发登录流程）
                var createJson = await HttpGetJson(
                    $"{API_Address}/login/qr/create?key={Uri.EscapeDataString(loginKey)}&timestamp={GetTimeStamp()}"
                ); //获取createjson

                // 3) 获取base64格式的二维码图片
                string base64String = keyJson["data"]["qrcode_img"]?.ToString();
                Console.WriteLine($"[Kugou] login key: {loginKey}");
                if (string.IsNullOrEmpty(loginKey))
                    return "登录失败：未获取到二维码 key。";

                // 4) 同时提供两种二维码显示方式
                if (!string.IsNullOrEmpty(base64String))
                {
                    await ts3Client.SendChannelMessage("正在生成二维码...");
                    Console.WriteLine($"[Kugou] login qrcode: {base64String}");

                    // 方式1：解析base64并设置为机器人头像
                    string[] img = base64String.Split(',');
                    if (img.Length > 1)
                    {
                        byte[] bytes = Convert.FromBase64String(img[1]);
                        Stream stream = new MemoryStream(bytes);
                        await tsFullClient.UploadAvatar(stream);
                        stream.Dispose();
                    }

                    await ts3Client.ChangeDescription("请用酷狗APP扫描二维码登录");
                }

                // 方式2：同时生成API二维码URL链接
                var qrApi = "https://qrcode.jp/qr?q="; // 二维码生成API
                var loginUrl = createJson["data"]["url"]?.ToString(); // 从createJson获取登录url
                if (!string.IsNullOrEmpty(loginUrl))
                {
                    var qrCodeUrl = $"[URL]{qrApi}{Uri.EscapeDataString(loginUrl)}[/URL]"; // 生成可以直接访问的二维码链接
                    await ts3Client.SendChannelMessage($"备用扫码方式：{qrCodeUrl}");
                }
                else
                {
                    await ts3Client.SendChannelMessage(
                        "请使用手机酷狗App扫码登录（扫码后请在手机上确认登录）"
                    );
                }

                // 5) 轮询扫码状态
                string cookieString = null;
                const int maxWaitSec = 120;
                var deadline = DateTimeOffset.UtcNow.AddSeconds(maxWaitSec);

                while (DateTimeOffset.UtcNow < deadline)
                {
                    await Task.Delay(1500);
                    var (statusCode, cookies) = await CheckLoginStatusWithCookies(
                        $"{API_Address}/login/qr/check?key={Uri.EscapeDataString(loginKey)}&timestamp={GetTimeStamp()}"
                    );
                    Console.WriteLine($"[Kugou] login status: {statusCode}");
                    // statusCode: 4(成功) / 2(已扫码待确认) / 1(待扫码)
                    if (statusCode == 4)
                    {
                        cookieString = cookies;
                        break;
                    }
                }

                if (string.IsNullOrEmpty(cookieString))
                {
                    await ts3Client.DeleteAvatar();
                    await ts3Client.ChangeDescription(""); // 清空描述
                    return "登录超时或未完成。请重试 !kugou login。";
                }

                // 4) 登录成功后清理头像
                await ts3Client.DeleteAvatar();
                await ts3Client.ChangeDescription(""); // 清空描述

                // 5) 保存完整的 Cookie 到对应TS用户的loginToken文件
                string tsId = invoker.ClientUid.ToString();
                string dataDir = Environment.GetFolderPath(
                    Environment.SpecialFolder.ApplicationData
                );
                string filePath = Path.Combine(dataDir, $"{tsId}_loginToken.txt");
                File.WriteAllText(filePath, cookieString ?? string.Empty);

                await ts3Client.SendChannelMessage("🆔登录成功：已保存 cookies。");
                return null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Kugou] login error: {ex}");
                return "登录失败：接口错误或网络异常。";
            }
        }

        [Command("kugou vip")]
        public async Task<string> CommandVipLogin(InvokerData invoker)
        {
            try
            {
                // 1) 申请登录 key
                var keyJson = await HttpGetJson(
                    $"{API_Address}/login/qr/key?timestamp={GetTimeStamp()}"
                ); //获取keyjson
                string loginKey = keyJson["data"]["qrcode"].ToString(); //从keyjson获取key

                // 2) 必需的createJson请求（触发登录流程）
                var createJson = await HttpGetJson(
                    $"{API_Address}/login/qr/create?key={Uri.EscapeDataString(loginKey)}&timestamp={GetTimeStamp()}"
                ); //获取createjson

                // 3) 获取base64格式的二维码图片
                string base64String = keyJson["data"]["qrcode_img"]?.ToString();
                Console.WriteLine($"[Kugou] vip login key: {loginKey}");
                if (string.IsNullOrEmpty(loginKey))
                    return "VIP登录失败：未获取到二维码 key。";

                // 4) 同时提供两种二维码显示方式
                if (!string.IsNullOrEmpty(base64String))
                {
                    await ts3Client.SendChannelMessage("正在生成VIP登录二维码...");
                    Console.WriteLine($"[Kugou] vip login qrcode: {base64String}");

                    // 方式1：解析base64并设置为机器人头像
                    string[] img = base64String.Split(',');
                    if (img.Length > 1)
                    {
                        byte[] bytes = Convert.FromBase64String(img[1]);
                        Stream stream = new MemoryStream(bytes);
                        await tsFullClient.UploadAvatar(stream);
                        stream.Dispose();
                    }

                    await ts3Client.ChangeDescription("请用酷狗VIP账号扫描二维码登录");
                }

                // 方式2：同时生成API二维码URL链接
                var qrApi = "https://qrcode.jp/qr?q="; // 二维码生成API
                var loginUrl = createJson["data"]["url"]?.ToString(); // 从createJson获取登录url
                if (!string.IsNullOrEmpty(loginUrl))
                {
                    var qrCodeUrl = $"[URL]{qrApi}{Uri.EscapeDataString(loginUrl)}[/URL]"; // 生成可以直接访问的二维码链接
                    await ts3Client.SendChannelMessage($"VIP备用扫码方式：{qrCodeUrl}");
                }
                else
                {
                    await ts3Client.SendChannelMessage(
                        "请使用手机酷狗VIP账号扫码登录（扫码后请在手机上确认登录）"
                    );
                }

                // 5) 轮询扫码状态
                string cookieString = null;
                const int maxWaitSec = 120;
                var deadline = DateTimeOffset.UtcNow.AddSeconds(maxWaitSec);

                while (DateTimeOffset.UtcNow < deadline)
                {
                    await Task.Delay(1500);
                    var (statusCode, cookies) = await CheckLoginStatusWithCookies(
                        $"{API_Address}/login/qr/check?key={Uri.EscapeDataString(loginKey)}&timestamp={GetTimeStamp()}"
                    );
                    Console.WriteLine($"[Kugou] vip login status: {statusCode}");
                    // statusCode: 4(成功) / 2(已扫码待确认) / 1(待扫码)
                    if (statusCode == 4)
                    {
                        cookieString = cookies;
                        break;
                    }
                }

                if (string.IsNullOrEmpty(cookieString))
                {
                    await ts3Client.DeleteAvatar();
                    await ts3Client.ChangeDescription(""); // 清空描述
                    return "VIP登录超时或未完成。请重试 !kugou vip。";
                }

                // 4) 登录成功后清理头像
                await ts3Client.DeleteAvatar();
                await ts3Client.ChangeDescription(""); // 清空描述

                // 5) 保存完整的 Cookie 到 vipToken.txt 到数据目录
                string dataDir = Environment.GetFolderPath(
                    Environment.SpecialFolder.ApplicationData
                );
                string filePath = Path.Combine(dataDir, "vipToken.txt");
                File.WriteAllText(filePath, cookieString ?? string.Empty);

                await ts3Client.SendChannelMessage("👑VIP登录成功：已保存VIP cookies。");
                return null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Kugou] vip login error: {ex}");
                return "VIP登录失败：接口错误或网络异常。";
            }
        }

        [Command("kugou list")]
        public async Task<string> CommandList(InvokerData invoker)
        {
            try
            {
                // 获取用户歌单列表，使用对应TS用户的cookie
                string tsId = invoker.ClientUid.ToString();
                var playlistJson = await HttpGetJson($"{API_Address}/user/playlist?timestamp={GetTimeStamp()}", true, tsId, false);
                var playlists = ParseKugouPlaylistList(playlistJson);

                if (playlists == null || playlists.Count == 0)
                {
                    return "未找到歌单，请确保已登录并拥有歌单。";
                }

                // 缓存给当前调用者
                string key = invoker.ClientUid.ToString();
                PlaylistCache[key] = playlists;

                // 输出格式
                var sb = new StringBuilder();
                sb.AppendLine("");
                sb.AppendLine("----");
                sb.AppendLine("📝您的歌单列表");
                
                for (int i = 0; i < playlists.Count; i++)
                {
                    var playlist = playlists[i];
                    sb.AppendLine($"{i + 1}. {playlist.Name} ({playlist.Count}首)");
                }
                
                sb.AppendLine("请输入 !kugou playlist 【序号】 [模式] 播放相应歌单");
                sb.AppendLine("模式参数：0或空=顺序播放，1=随机播放");
                sb.AppendLine("----");

                return sb.ToString();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Kugou] list error: {ex}");
                return "获取歌单失败：接口错误或网络异常，请确保已登录。";
            }
        }

        [Command("kugou playlist")]
        public async Task<string> CommandPlaylist(InvokerData invoker, string indexText = null, string modeText = null)
        {
            string key = invoker.ClientUid.ToString();
            if (
                !PlaylistCache.TryGetValue(key, out var playlistList)
                || playlistList == null
                || playlistList.Count == 0
            )
            {
                return "没有可播放的歌单，请先使用 !kugou list 获取歌单列表。";
            }

            int index = 1; // 默认第一个歌单
            if (!string.IsNullOrWhiteSpace(indexText))
            {
                if (!int.TryParse(indexText, out index) || index < 1 || index > playlistList.Count)
                {
                    return $"播放失败：无效的序号（1~{playlistList.Count}）。";
                }
            }

            // 解析播放模式：0或空 = 顺序播放，1 = 随机播放
            bool isRandomMode = false;
            if (!string.IsNullOrWhiteSpace(modeText))
            {
                if (int.TryParse(modeText, out int mode))
                {
                    isRandomMode = (mode == 1);
                }
            }

            var playlist = playlistList[index - 1];

            try
            {
                // 获取歌单详情，使用对应TS用户的cookie
                string tsId = invoker.ClientUid.ToString();
                string url = $"{API_Address}/playlist/track/all?id={Uri.EscapeDataString(playlist.GlobalCollectionId)}";
                Console.WriteLine($"[Kugou] Requesting playlist tracks: {url}");
                var trackJson = await HttpGetJson(url, true, tsId, false);
                var songs = ParseKugouTrackList(trackJson);

                if (songs == null || songs.Count == 0)
                {
                    return $"歌单 '{playlist.Name}' 为空或获取失败。";
                }

                // 根据模式处理歌曲列表
                string modeDisplayText = isRandomMode ? "🔀随机" : "▶️顺序";
                if (isRandomMode)
                {
                    // 使用 Fisher-Yates 洗牌算法打乱歌曲顺序
                    var random = new Random();
                    for (int i = songs.Count - 1; i > 0; i--)
                    {
                        int j = random.Next(0, i + 1);
                        var temp = songs[i];
                        songs[i] = songs[j];
                        songs[j] = temp;
                    }
                }

                await ts3Client.SendChannelMessage($"🎵 开始{modeDisplayText}播放歌单：{playlist.Name} ({songs.Count}首)");

                // 播放第一首歌（优先尝试VIP）
                var firstSong = songs[0];
                string playUrl = await GetSongPlayUrlAsync(firstSong, true);
                bool isVipPlaylist = !string.IsNullOrWhiteSpace(playUrl);
                
                if (string.IsNullOrWhiteSpace(playUrl))
                {
                    playUrl = await GetSongPlayUrlAsync(firstSong, false);
                }
                
                if (!string.IsNullOrWhiteSpace(playUrl))
                {
                    string playMode = isVipPlaylist ? "👑VIP" : "🎵";
                    await ts3Client.SendChannelMessage($"{playMode} 正在播放：{firstSong.Artist} - {firstSong.Title}");
                    await MainCommands.CommandPlay(playManager, invoker, playUrl);
                }

                // 将剩余歌曲添加到播放队列（优先尝试VIP）
                for (int i = 1; i < songs.Count; i++)
                {
                    var song = songs[i];
                    try
                    {
                        string songUrl = await GetSongPlayUrlAsync(song, isVipPlaylist);
                        if (string.IsNullOrWhiteSpace(songUrl) && isVipPlaylist)
                        {
                            // VIP失败，尝试普通模式
                            songUrl = await GetSongPlayUrlAsync(song, false);
                        }
                        
                        if (!string.IsNullOrWhiteSpace(songUrl))
                        {
                            await MainCommands.CommandAdd(playManager, invoker, songUrl);
                            await Task.Delay(500); // 避免请求过快
                        }
                        else
                        {
                            Console.WriteLine($"[Kugou] Failed to get URL for song: {song.Title}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[Kugou] Error adding song {song.Title}: {ex.Message}");
                    }
                }

                string playlistMode = isVipPlaylist ? "👑VIP" : "✅";
                string finalModeText = isRandomMode ? "随机" : "顺序";
                await ts3Client.SendChannelMessage($"{playlistMode} 歌单 '{playlist.Name}' 已{finalModeText}添加到播放队列");
                return null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Kugou] playlist error: {ex}");
                return "播放歌单失败：接口错误或网络异常。";
            }
        }

        // ============ HTTP & 解析工具 ============

        private static async Task<JObject> HttpGetJson(string url, bool useCookie = true, string tsId = null, bool useVip = false)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            // 如需 Referer / UA，可在此加 headers
            // req.Headers.Referrer = new Uri("https://www.kugou.com/");

            // 如果需要使用cookie，则从文件读取并添加到请求头
            if (useCookie)
            {
                string cookies = GetSavedCookies(tsId, useVip);
                if (!string.IsNullOrEmpty(cookies))
                {
                    req.Headers.Add("Cookie", cookies);
                }
            }

            var resp = await http.SendAsync(req);
            resp.EnsureSuccessStatusCode();
            string json = await resp.Content.ReadAsStringAsync();
            return JObject.Parse(json);
        }

        private static async Task<(int statusCode, string cookies)> CheckLoginStatusWithCookies(string url)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            var resp = await http.SendAsync(req);
            resp.EnsureSuccessStatusCode();
            string json = await resp.Content.ReadAsStringAsync();
            var jo = JObject.Parse(json);
            
            var status = ParseLoginStatus(jo);
            string cookiesString = null;
            
            // 如果登录成功，获取响应头中的所有cookies
            if (status.StatusCode == 4)
            {
                var cookieList = new List<string>();
                
                // 尝试从不同的响应头中获取cookies
                if (resp.Headers.TryGetValues("Set-Cookie", out var setCookieHeaders))
                {
                    cookieList.AddRange(setCookieHeaders);
                }
                
                // 某些API可能使用其他头
                if (resp.Headers.TryGetValues("Cookie", out var cookieHeaders))
                {
                    cookieList.AddRange(cookieHeaders);
                }
                
                if (cookieList.Count > 0)
                {
                    cookiesString = string.Join("; ", cookieList);
                }
            }
            
            return (status.StatusCode, cookiesString);
        }

        private static async Task<JObject> HttpPostJson(string url, JObject body = null)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Content = new StringContent(
                (body ?? new JObject()).ToString(),
                Encoding.UTF8,
                "application/json"
            );
            var resp = await http.SendAsync(req);
            resp.EnsureSuccessStatusCode();
            string json = await resp.Content.ReadAsStringAsync();
            return JObject.Parse(json);
        }

        private static async Task<List<KugouSongItem>> SearchSongsAsync(string keyword, string tsId = null, bool useVip = false)
        {
            // 根据你的 API 文档调整路径/参数：
            // 常见：/search/song?keywords= xxx
            string url =
                $"{API_Address}/search/song?keywords={Uri.EscapeDataString(keyword)}&pagesize=10&page=1&type=song";
            var jo = await HttpGetJson(url, true, tsId, useVip); // 使用对应的cookie
            return ParseKugouSearchList(jo);
        }

        private static async Task<string> GetSongPlayUrlAsync(KugouSongItem s, bool useVip = false)
        {
            // 按你的服务网关调整；常见方式是通过 hash/albumId 拿到直链
            // 例如：/song/url?hash=xxx&album_id=yyy
            string url =
                $"{API_Address}/song/url?hash={Uri.EscapeDataString(s.Hash ?? "")}&album_id={Uri.EscapeDataString(s.AlbumId ?? "")}&free_part=true&timestamp={GetTimeStamp()}";
            Console.WriteLine($"[Kugou] GetSongPlayUrlAsync: {url}");
            var jo = await HttpGetJson(url, true, null, useVip);
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
                jo.SelectToken("data.lists")
                ?? jo.SelectToken("data.list")
                ?? jo.SelectToken("result.songs")
                ?? jo.SelectToken("songs")
                ?? jo.SelectToken("data.songs");

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

                    // 提取 Hash，优先级：HQ.Hash > SQ.Hash > FileHash
                    string hash = null;
                    var sqHash = it.SelectToken("SQ.Hash")?.ToString();
                    var hqHash = it.SelectToken("HQ.Hash")?.ToString();
                    var fileHash = it.Value<string>("FileHash");

                    if (!string.IsNullOrWhiteSpace(hqHash))
                        hash = hqHash;
                    else if (!string.IsNullOrWhiteSpace(sqHash))
                        hash = sqHash;
                    else
                        hash = fileHash;

                    // 提取专辑ID
                    string albumId = it.Value<string>("AlbumID");

                    // 检查必要字段
                    if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(artist))
                        continue;

                    list.Add(
                        new KugouSongItem
                        {
                            Title = title?.Trim() ?? "未知标题",
                            Artist = artist?.Trim() ?? "未知歌手",
                            Hash = hash?.Trim(),
                            AlbumId = albumId?.Trim(),
                        }
                    );
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
                jo.SelectToken("data.play_url")
                ?? jo.SelectToken("data.url")
                ?? jo.SelectToken("url")
                ?? jo.SelectToken("data.data.play_url");

            return token?.ToString();
        }

        private static string ParseLoginKey(JObject jo)
        {
            // 常见返回：{ "data": { "key": "xxxx" } } 或 { "data": { "unikey": "xxxx" } }
            var t =
                jo.SelectToken("data.key")
                ?? jo.SelectToken("data.unikey")
                ?? jo.SelectToken("key")
                ?? jo.SelectToken("unikey");
            return t?.ToString();
        }

        private static string ParseQrBase64(JObject jo)
        {
            // 常见返回：{ "data": { "qrimg": "data:image/png;base64,..." } } 或 { "data": { "image": "..." } }
            var t =
                jo.SelectToken("data.qrimg")
                ?? jo.SelectToken("data.image")
                ?? jo.SelectToken("qrimg")
                ?? jo.SelectToken("image");
            return t?.ToString();
        }

        private static List<KugouPlaylistItem> ParseKugouPlaylistList(JObject jo)
        {
            // 根据playlist.json解析歌单列表
            var list = new List<KugouPlaylistItem>();

            JToken arr = jo.SelectToken("data.info");

            if (arr is JArray ja)
            {
                foreach (var it in ja)
                {
                    string name = it.Value<string>("name");
                    string globalCollectionId = it.Value<string>("global_collection_id");
                    int count = it.Value<int>("count");

                    if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(globalCollectionId))
                    {
                        list.Add(new KugouPlaylistItem
                        {
                            Name = name,
                            GlobalCollectionId = globalCollectionId,
                            Count = count
                        });
                    }
                }
            }

            return list;
        }

        private static List<KugouSongItem> ParseKugouTrackList(JObject jo)
        {
            // 根据track.json解析歌单中的歌曲列表
            var list = new List<KugouSongItem>();

            JToken arr = jo.SelectToken("data.songs");

            if (arr is JArray ja)
            {
                foreach (var it in ja)
                {
                    string name = it.Value<string>("name");
                    string hash = it.Value<string>("hash");
                    string albumId = it.Value<string>("album_id");

                    // 从name字段中提取歌手和歌曲名 (格式通常是 "歌手 - 歌曲名")
                    string artist = "";
                    string title = name;

                    if (!string.IsNullOrWhiteSpace(name) && name.Contains(" - "))
                    {
                        var parts = name.Split(new[] { " - " }, 2, StringSplitOptions.None);
                        if (parts.Length == 2)
                        {
                            artist = parts[0].Trim();
                            title = parts[1].Trim();
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(hash) && !string.IsNullOrWhiteSpace(albumId))
                    {
                        list.Add(new KugouSongItem
                        {
                            Title = title,
                            Artist = artist,
                            Hash = hash,
                            AlbumId = albumId
                        });
                    }
                }
            }

            return list;
        }

        private static LoginStatus ParseLoginStatus(JObject jo)
        {
            // 典型：{ "code": 802/803, "data": { "token": "...", "cookie": "..." } }
            var status = new LoginStatus();
            int code = jo["data"]["status"]?.Value<int>() ?? -1;
            status.StatusCode = code;

            string token = jo["data"]?["token"]?.ToString();

            status.TokenOrCookie = token;
            return status;
        }

        private static string GetInvokerKey(InvokerData invoker)
        {
            return invoker.ClientUid.ToString();
        }

        private static string GetSavedCookies(string tsId = null, bool useVip = false)
        {
            try
            {
                string dataDir = Environment.GetFolderPath(
                    Environment.SpecialFolder.ApplicationData
                );
                
                string fileName;
                if (useVip)
                {
                    fileName = "vipToken.txt";
                }
                else if (!string.IsNullOrEmpty(tsId))
                {
                    fileName = $"{tsId}_loginToken.txt";
                }
                else
                {
                    // 兼容旧版本，优先使用vip token
                    string vipPath = Path.Combine(dataDir, "vipToken.txt");
                    if (File.Exists(vipPath))
                    {
                        fileName = "vipToken.txt";
                    }
                    else
                    {
                        fileName = "loginToken.txt"; // 旧版本兼容
                    }
                }

                string filePath = Path.Combine(dataDir, fileName);

                if (File.Exists(filePath))
                {
                    string rawCookies = File.ReadAllText(filePath).Trim();
                    
                    // 处理从响应头获取的完整cookie字符串，提取有效的name=value部分
                    if (!string.IsNullOrEmpty(rawCookies))
                    {
                        return CleanCookieString(rawCookies);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Kugou] Error reading cookies: {ex}");
            }
            return null;
        }

        private static string CleanCookieString(string rawCookies)
        {
            if (string.IsNullOrWhiteSpace(rawCookies))
                return null;

            var cleanedCookies = new List<string>();
            
            // Set-Cookie头可能包含多个独立的cookie，每个用"; "连接
            // 但每个Set-Cookie头内部也用";"分隔属性
            // 我们需要分别处理每个Set-Cookie头
            var setCookieHeaders = rawCookies.Split(new[] { "; " }, StringSplitOptions.RemoveEmptyEntries);
            
            foreach (var setCookieHeader in setCookieHeaders)
            {
                // 每个Set-Cookie头的第一部分是 name=value，其余是属性
                var parts = setCookieHeader.Split(';');
                if (parts.Length > 0)
                {
                    var cookieNameValue = parts[0].Trim();
                    
                    // 验证这是一个有效的 name=value 格式的cookie
                    if (cookieNameValue.Contains("=") && 
                        !cookieNameValue.StartsWith("Path=", StringComparison.OrdinalIgnoreCase) && 
                        !cookieNameValue.StartsWith("Domain=", StringComparison.OrdinalIgnoreCase) && 
                        !cookieNameValue.StartsWith("Expires=", StringComparison.OrdinalIgnoreCase) &&
                        !cookieNameValue.StartsWith("Max-Age=", StringComparison.OrdinalIgnoreCase) && 
                        !cookieNameValue.Equals("HttpOnly", StringComparison.OrdinalIgnoreCase) &&
                        !cookieNameValue.Equals("Secure", StringComparison.OrdinalIgnoreCase) && 
                        !cookieNameValue.StartsWith("SameSite=", StringComparison.OrdinalIgnoreCase))
                    {
                        cleanedCookies.Add(cookieNameValue);
                    }
                }
            }

            return cleanedCookies.Count > 0 ? string.Join("; ", cleanedCookies) : null;
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

        private class KugouPlaylistItem
        {
            public string Name { get; set; }
            public string GlobalCollectionId { get; set; }
            public int Count { get; set; }
        }

        private class LoginStatus
        {
            public int StatusCode { get; set; }
            public string TokenOrCookie { get; set; }
        }
    }
}
