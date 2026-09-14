using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Interop;
using Microsoft.Win32;
using Drawing = System.Drawing;
using Drawing2D = System.Drawing.Drawing2D;

namespace LiveBell
{
    public sealed class AppState
    {
        public AppSettings Settings = new AppSettings();
        public List<Streamer> Streamers = new List<Streamer>();
    }

    public sealed class AppSettings
    {
        public bool AutoStart = false;
        public bool ResidentInTray = true;
        public bool WindowsNotification = false;
        public int IntervalSeconds = 60;
        public int ToastSeconds = 8;
        public string CustomSoundPath = "";

        public AppSettings Copy()
        {
            return new AppSettings
            {
                AutoStart = AutoStart,
                ResidentInTray = ResidentInTray,
                WindowsNotification = WindowsNotification,
                IntervalSeconds = IntervalSeconds,
                ToastSeconds = ToastSeconds,
                CustomSoundPath = CustomSoundPath
            };
        }
    }

    public sealed class Streamer : INotifyPropertyChanged
    {
        private string _name = "新主播";
        private string _platform = "bilibili";
        private string _roomId = "";
        private string _title = "等待读取直播间信息";
        private string _state = "等待检查";
        private string _avatarPath = "";
        private bool _isCheckingEnabled = true;
        private ImageSource _avatarImage;

        public string Id = Guid.NewGuid().ToString("N");
        public string Name { get { return _name; } set { _name = value; Changed("Name"); } }
        public string Platform { get { return _platform; } set { _platform = value; Changed("Platform"); Changed("PlatformText"); } }
        public string RoomId { get { return _roomId; } set { _roomId = value; Changed("RoomId"); } }
        public string Title { get { return _title; } set { _title = value; Changed("Title"); } }
        public string State { get { return _state; } set { _state = value; Changed("State"); Changed("StateText"); Changed("StateBrush"); } }
        public string AvatarUrl = "";
        public string AvatarPath { get { return _avatarPath; } set { _avatarPath = value ?? ""; Changed("AvatarPath"); LoadAvatar(); } }
        public string Url = "";
        public string CustomSoundPath = "";
        public bool IsCheckingEnabled
        {
            get { return _isCheckingEnabled; }
            set
            {
                _isCheckingEnabled = value;
                Changed("IsCheckingEnabled");
                Changed("CheckingButtonText");
                Changed("CheckingButtonBackground");
                Changed("CheckingButtonForeground");
            }
        }
        public bool IsLive;
        public bool HasChecked;
        public string LastError = "";
        [ScriptIgnore]
        public ImageSource AvatarImage { get { return _avatarImage; } private set { _avatarImage = value; Changed("AvatarImage"); } }
        [ScriptIgnore]
        public string PlatformText { get { return String.Equals(Platform, "douyu", StringComparison.OrdinalIgnoreCase) ? "斗鱼" : "B站"; } }
        [ScriptIgnore]
        public string StateText { get { return State; } }
        [ScriptIgnore]
        public string CheckingButtonText { get { return IsCheckingEnabled ? "检查中" : "已暂停"; } }
        [ScriptIgnore]
        public string CheckingButtonBackground { get { return IsCheckingEnabled ? "#EAF8F0" : "#F1F3F7"; } }
        [ScriptIgnore]
        public string CheckingButtonForeground { get { return IsCheckingEnabled ? "#24945E" : "#71809A"; } }
        [ScriptIgnore]
        public string StateBrush
        {
            get
            {
                if (State == "直播中") return "#EF4F84";
                if (State == "未开播") return "#46A675";
                if (State == "视频轮播") return "#8A63C6";
                if (State == "读取中") return "#5B7ACB";
                return State == "连接异常" || State == "状态待确认" ? "#D18434" : "#70809A";
            }
        }

        public void LoadAvatar()
        {
            AvatarImage = ImageTools.Load(AvatarPath);
        }

        private void Changed(string name)
        {
            PropertyChangedEventHandler changed = PropertyChanged;
            if (changed != null) changed(this, new PropertyChangedEventArgs(name));
        }

        public event PropertyChangedEventHandler PropertyChanged;
    }

    internal static class ImageTools
    {
        public static ImageSource Load(string path)
        {
            try
            {
                if (String.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
                BitmapImage image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.UriSource = new Uri(path, UriKind.Absolute);
                image.EndInit();
                image.Freeze();
                return image;
            }
            catch { return null; }
        }
    }

    internal static class LocalData
    {
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
        public static readonly string Root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data");
        public static readonly string StateFile = Path.Combine(Root, "开播铃数据.json");
        public static readonly string AvatarFolder = Path.Combine(Root, "avatars");
        public static readonly string SoundFolder = Path.Combine(Root, "sounds");

        public static AppState Load()
        {
            try
            {
                if (File.Exists(StateFile))
                {
                    AppState state = Json.Deserialize<AppState>(File.ReadAllText(StateFile));
                    if (state != null)
                    {
                        if (state.Settings == null) state.Settings = new AppSettings();
                        if (state.Settings.ToastSeconds < 3 || state.Settings.ToastSeconds > 3600) state.Settings.ToastSeconds = 8;
                        if (state.Streamers == null) state.Streamers = new List<Streamer>();
                        return state;
                    }
                }
            }
            catch { }
            return new AppState();
        }

        public static void Save(AppState state)
        {
            Directory.CreateDirectory(Root);
            Directory.CreateDirectory(AvatarFolder);
            File.WriteAllText(StateFile, Json.Serialize(state));
        }

        public static string AvatarFile(Streamer item)
        {
            return Path.Combine(AvatarFolder, item.Id + ".image");
        }

        public static string ImportSound(string sourceFile, string soundName = "提醒声音")
        {
            if (String.IsNullOrWhiteSpace(sourceFile) || !File.Exists(sourceFile))
                throw new InvalidOperationException("未找到选择的声音文件。");
            string extension = Path.GetExtension(sourceFile);
            if (String.IsNullOrWhiteSpace(extension)) extension = ".wav";
            Directory.CreateDirectory(SoundFolder);
            string target = Path.Combine(SoundFolder, soundName + extension);
            File.Copy(sourceFile, target, true);
            return target;
        }
    }

    internal sealed class RoomResult
    {
        public bool ok;
        public string message;
        public string platform;
        public string roomId;
        public string name;
        public string avatarUrl;
        public string title;
        public bool isLive;
        public bool liveConfirmed;
        public bool videoLoop;
        public int viewers;
        public string area;
        public string url;
    }

    // 直播检测完全由 .NET 完成，不需要随软件携带 node.exe。
    internal static class RoomClient
    {
        private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/130 Safari/537.36";
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();

        public static Task<RoomResult> QueryAsync(Streamer streamer)
        {
            return Task.Run(delegate { return Query(streamer.Platform, streamer.RoomId); });
        }

        public static Task<List<RoomResult>> QueryManyAsync(IEnumerable<Streamer> streamers)
        {
            return Task.Run(delegate
            {
                List<RoomResult> results = new List<RoomResult>();
                foreach (Streamer streamer in streamers)
                {
                    try { results.Add(Query(streamer.Platform, streamer.RoomId)); }
                    catch (Exception ex)
                    {
                        results.Add(new RoomResult { ok = false, platform = streamer.Platform, roomId = streamer.RoomId, message = Text(ex.Message, "暂时无法读取，请稍后刷新") });
                    }
                }
                return results;
            });
        }

        public static Task DownloadAvatarAsync(string url, string target)
        {
            return Task.Run(delegate
            {
                if (String.IsNullOrWhiteSpace(url)) throw new InvalidOperationException("缺少头像地址。");
                if (url.StartsWith("//")) url = "https:" + url;
                byte[] bytes = DownloadBytes(url, "https://live.bilibili.com/");
                if (bytes == null || bytes.Length < 100) throw new InvalidOperationException("头像文件不完整。");
                string folder = Path.GetDirectoryName(target);
                if (!String.IsNullOrWhiteSpace(folder)) Directory.CreateDirectory(folder);
                File.WriteAllBytes(target, bytes);
            });
        }

        private static RoomResult Query(string platform, string roomId)
        {
            if (String.Equals(platform, "bilibili", StringComparison.OrdinalIgnoreCase)) return Bilibili(roomId);
            if (String.Equals(platform, "douyu", StringComparison.OrdinalIgnoreCase)) return Douyu(roomId);
            throw new InvalidOperationException("未知直播平台。");
        }

        private static RoomResult Bilibili(string roomId)
        {
            const string referer = "https://live.bilibili.com/";
            Dictionary<string, object> root = GetJson("https://api.live.bilibili.com/room/v1/Room/get_info?room_id=" + Uri.EscapeDataString(roomId), referer);
            if (Number(root, "code") != 0) throw new InvalidOperationException(Text(TextValue(root, "message"), "未找到这个 B站直播间。"));
            Dictionary<string, object> data = Object(root, "data");
            if (data == null) throw new InvalidOperationException("B站没有返回直播间数据。");
            string actualRoomId = Text(TextValue(data, "room_id"), roomId);
            Dictionary<string, object> anchor = null;
            try
            {
                Dictionary<string, object> owner = GetJson("https://api.live.bilibili.com/live_user/v1/UserInfo/get_anchor_in_room?roomid=" + Uri.EscapeDataString(actualRoomId), referer);
                anchor = Object(Object(owner, "data"), "info");
            }
            catch { }
            if (String.IsNullOrWhiteSpace(TextValue(anchor, "face")) && !String.IsNullOrWhiteSpace(TextValue(data, "uid")))
            {
                try
                {
                    Dictionary<string, object> profile = GetJson("https://api.bilibili.com/x/space/acc/info?mid=" + Uri.EscapeDataString(TextValue(data, "uid")), referer);
                    Dictionary<string, object> profileData = Object(profile, "data");
                    if (profileData != null) anchor = profileData;
                }
                catch { }
            }
            return new RoomResult
            {
                ok = true,
                platform = "bilibili",
                roomId = actualRoomId,
                name = First(TextValue(anchor, "uname"), TextValue(data, "uname"), "B站主播"),
                avatarUrl = First(TextValue(anchor, "face"), TextValue(data, "face"), ""),
                title = Text(TextValue(data, "title"), "主播暂未填写直播标题"),
                isLive = Number(data, "live_status") == 1,
                liveConfirmed = true,
                videoLoop = false,
                viewers = Number(data, "online"),
                area = Join(TextValue(data, "parent_area_name"), TextValue(data, "area_name"), "直播"),
                url = "https://live.bilibili.com/" + actualRoomId
            };
        }

        private static RoomResult Douyu(string roomId)
        {
            const string referer = "https://www.douyu.com/";
            Dictionary<string, object> room = null;
            try
            {
                string mobile = GetText("https://m.douyu.com/" + Uri.EscapeDataString(roomId), referer);
                Match match = Regex.Match(mobile, @"<script id=""vike_pageContext"" type=""application/json"">(?<json>[\s\S]*?)</script>", RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    Dictionary<string, object> context = JsonObject(match.Groups["json"].Value);
                    room = PathObject(context, "pageProps", "room", "roomInfo", "roomInfo");
                }
            }
            catch { }

            if (room != null)
            {
                string actualRoomId = First(TextValue(room, "vipId"), TextValue(room, "rid"), roomId);
                string detailRoomId = First(TextValue(room, "rid"), TextValue(room, "vipId"), roomId);
                Dictionary<string, object> detail = null;
                try
                {
                    Dictionary<string, object> source = GetJson("https://www.douyu.com/betard/" + Uri.EscapeDataString(detailRoomId), referer);
                    detail = FirstObject(Object(source, "room"), Object(Object(source, "data"), "room"), Object(source, "data"));
                }
                catch { }
                object loopValue = Value(detail, "videoLoop", "video_loop", "video_loop_type");
                object showStatus = Value(detail, "show_status", "showStatus");
                bool detailHasNoVideoLoop = IsFalse(loopValue);
                bool videoLoop = loopValue != null && !detailHasNoVideoLoop;
                // The mobile page reports isLive for video loops too. Only the
                // detail response can confirm both a live broadcast and a non-loop video.
                bool liveConfirmed = detail != null && loopValue != null && showStatus != null;
                return new RoomResult
                {
                    ok = true,
                    platform = "douyu",
                    roomId = actualRoomId,
                    name = Text(TextValue(room, "nickname"), "斗鱼主播"),
                    avatarUrl = TextValue(room, "avatar"),
                    title = First(TextValue(detail, "room_name"), TextValue(room, "roomName"), "主播暂未填写直播标题"),
                    isLive = liveConfirmed && detailHasNoVideoLoop && IsTrue(showStatus),
                    liveConfirmed = liveConfirmed,
                    videoLoop = videoLoop,
                    viewers = Number(room, "hn", "online"),
                    area = Text(TextValue(room, "cate2Name"), "直播"),
                    url = "https://www.douyu.com/" + actualRoomId
                };
            }

            bool fromBetard = true;
            try
            {
                Dictionary<string, object> source = GetJson("https://www.douyu.com/betard/" + Uri.EscapeDataString(roomId), referer);
                room = FirstObject(Object(source, "room"), Object(Object(source, "data"), "room"), Object(source, "data"));
            }
            catch
            {
                fromBetard = false;
                Dictionary<string, object> fallback = GetJson("https://open.douyucdn.cn/api/RoomApi/room/" + Uri.EscapeDataString(roomId), referer);
                room = Object(fallback, "data");
            }
            if (room == null) throw new InvalidOperationException("斗鱼没有找到这个直播间，可能房间号已失效。 ");
            string resolvedRoomId = First(TextValue(room, "room_id"), TextValue(room, "rid"), roomId);
            object loop = Value(room, "videoLoop", "video_loop", "video_loop_type");
            bool fallbackHasNoVideoLoop = IsFalse(loop);
            bool isVideoLoop = loop != null && !fallbackHasNoVideoLoop;
            bool confirmed = fromBetard && loop != null;
            return new RoomResult
            {
                ok = true,
                platform = "douyu",
                roomId = resolvedRoomId,
                name = First(TextValue(room, "owner_name"), TextValue(room, "nickname"), "斗鱼主播"),
                avatarUrl = First(TextValue(room, "owner_avatar"), TextValue(room, "avatar"), TextValue(room, "owner_pic"), ""),
                title = First(TextValue(room, "room_name"), TextValue(room, "show_title"), "主播暂未填写直播标题"),
                isLive = confirmed && fallbackHasNoVideoLoop && IsTrue(Value(room, "show_status", "room_status")),
                liveConfirmed = confirmed,
                videoLoop = isVideoLoop,
                viewers = Number(room, "online", "online_num"),
                area = Join(TextValue(room, "cate1_name"), First(TextValue(room, "cate2_name"), TextValue(room, "cate_name"), ""), "直播"),
                url = "https://www.douyu.com/" + resolvedRoomId
            };
        }

        private static Dictionary<string, object> GetJson(string url, string referer)
        {
            return JsonObject(GetText(url, referer));
        }

        private static string GetText(string url, string referer)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            using (NativeWebClient client = new NativeWebClient())
            {
                client.Encoding = System.Text.Encoding.UTF8;
                client.Headers[HttpRequestHeader.UserAgent] = UserAgent;
                client.Headers[HttpRequestHeader.Accept] = "application/json, text/plain, */*";
                client.Headers[HttpRequestHeader.AcceptLanguage] = "zh-CN,zh;q=0.9";
                client.Headers[HttpRequestHeader.Referer] = referer;
                return client.DownloadString(url);
            }
        }

        private static byte[] DownloadBytes(string url, string referer)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            using (NativeWebClient client = new NativeWebClient())
            {
                client.Headers[HttpRequestHeader.UserAgent] = UserAgent;
                client.Headers[HttpRequestHeader.Referer] = referer;
                return client.DownloadData(url);
            }
        }

        private static Dictionary<string, object> JsonObject(string text)
        {
            Dictionary<string, object> value = Json.DeserializeObject(text) as Dictionary<string, object>;
            if (value == null) throw new InvalidOperationException("平台暂时没有返回直播间数据。 ");
            return value;
        }

        private static Dictionary<string, object> PathObject(Dictionary<string, object> value, params string[] path)
        {
            Dictionary<string, object> current = value;
            foreach (string key in path)
            {
                current = Object(current, key);
                if (current == null) return null;
            }
            return current;
        }

        private static Dictionary<string, object> Object(Dictionary<string, object> value, string key)
        {
            if (value == null || String.IsNullOrWhiteSpace(key)) return null;
            object found;
            return value.TryGetValue(key, out found) ? found as Dictionary<string, object> : null;
        }

        private static Dictionary<string, object> FirstObject(params Dictionary<string, object>[] values)
        {
            foreach (Dictionary<string, object> value in values) if (value != null) return value;
            return null;
        }

        private static object Value(Dictionary<string, object> value, params string[] keys)
        {
            if (value == null) return null;
            foreach (string key in keys)
            {
                object found;
                if (value.TryGetValue(key, out found) && found != null) return found;
            }
            return null;
        }

        private static string TextValue(Dictionary<string, object> value, params string[] keys)
        {
            object found = Value(value, keys);
            return found == null ? "" : Convert.ToString(found, CultureInfo.InvariantCulture).Trim();
        }

        private static int Number(Dictionary<string, object> value, params string[] keys)
        {
            int number;
            return Int32.TryParse(TextValue(value, keys), NumberStyles.Any, CultureInfo.InvariantCulture, out number) ? number : 0;
        }

        private static bool IsTrue(object value)
        {
            string text = value == null ? "" : Convert.ToString(value, CultureInfo.InvariantCulture).Trim();
            return text == "1" || String.Equals(text, "true", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsFalse(object value)
        {
            string text = value == null ? "" : Convert.ToString(value, CultureInfo.InvariantCulture).Trim();
            return text == "0" || String.Equals(text, "false", StringComparison.OrdinalIgnoreCase);
        }

        private static string First(params string[] values)
        {
            foreach (string value in values) if (!String.IsNullOrWhiteSpace(value)) return value.Trim();
            return "";
        }

        private static string Text(string value, string fallback)
        {
            return String.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        }

        private static string Join(string first, string second, string fallback)
        {
            List<string> values = new List<string>();
            if (!String.IsNullOrWhiteSpace(first)) values.Add(first.Trim());
            if (!String.IsNullOrWhiteSpace(second)) values.Add(second.Trim());
            return values.Count == 0 ? fallback : String.Join(" · ", values.ToArray());
        }

        private sealed class NativeWebClient : WebClient
        {
            protected override WebRequest GetWebRequest(Uri address)
            {
                HttpWebRequest request = base.GetWebRequest(address) as HttpWebRequest;
                if (request != null)
                {
                    request.Timeout = 12000;
                    request.ReadWriteTimeout = 12000;
                    request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
                }
                return request;
            }
        }
    }

    internal static class AddressParser
    {
        public static bool TryParse(string value, string selectedPlatform, out string platform, out string roomId)
        {
            platform = selectedPlatform;
            roomId = (value ?? "").Trim();
            if (roomId.Length == 0) return false;
            if (roomId.IndexOf("douyu.com", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                platform = "douyu";
                Match match = Regex.Match(roomId, @"douyu\.com/([^/?#]+)", RegexOptions.IgnoreCase);
                if (!match.Success) return false;
                roomId = match.Groups[1].Value;
            }
            else if (roomId.IndexOf("bilibili.com", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                platform = "bilibili";
                Match match = Regex.Match(roomId, @"live\.bilibili\.com/(\d+)", RegexOptions.IgnoreCase);
                if (!match.Success) return false;
                roomId = match.Groups[1].Value;
            }

            roomId = roomId.Trim().Trim('/');
            return roomId.Length > 0;
        }
    }

    internal static class AutoStartService
    {
        private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string Name = "开播铃";

        public static void Set(bool enabled)
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(KeyPath, true))
            {
                if (key == null) throw new InvalidOperationException("无法写入开机启动设置。");
                if (enabled) key.SetValue(Name, "\"" + Process.GetCurrentProcess().MainModule.FileName + "\" --background");
                else key.DeleteValue(Name, false);
            }
        }
    }

    internal static class SoundService
    {
        private static readonly Queue<string> pendingSounds = new Queue<string>();
        private static MediaPlayer player;
        private static bool playing;
        private static System.Windows.Threading.DispatcherTimer systemSoundTimer;

        public static void Play(string customPath)
        {
            pendingSounds.Enqueue(!String.IsNullOrWhiteSpace(customPath) && File.Exists(customPath) ? customPath : "");
            if (!playing) PlayNext();
        }

        private static void PlayNext()
        {
            if (pendingSounds.Count == 0)
            {
                playing = false;
                return;
            }
            playing = true;
            string customPath = pendingSounds.Dequeue();
            try
            {
                if (!String.IsNullOrWhiteSpace(customPath) && File.Exists(customPath))
                {
                    if (player != null) player.Close();
                    player = new MediaPlayer();
                    player.MediaEnded += delegate { PlayNext(); };
                    player.MediaFailed += delegate { PlayNext(); };
                    player.Open(new Uri(customPath, UriKind.Absolute));
                    player.Volume = 1;
                    player.Play();
                    return;
                }
            }
            catch { }
            try { System.Media.SystemSounds.Asterisk.Play(); } catch { }
            if (systemSoundTimer != null) systemSoundTimer.Stop();
            systemSoundTimer = new System.Windows.Threading.DispatcherTimer();
            systemSoundTimer.Interval = TimeSpan.FromMilliseconds(800);
            systemSoundTimer.Tick += delegate
            {
                systemSoundTimer.Stop();
                PlayNext();
            };
            systemSoundTimer.Start();
        }
    }

    internal static class TrayIconFactory
    {
        private static Drawing.Bitmap bitmap;
        private static Drawing.Icon avatar;

        public static Drawing.Icon GetAvatar()
        {
            if (avatar != null) return avatar;
            string imageFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "avatar-icon.png");
            if (!File.Exists(imageFile)) return Drawing.SystemIcons.Application;

            using (Drawing.Bitmap source = new Drawing.Bitmap(imageFile))
            {
                bitmap = new Drawing.Bitmap(32, 32, Drawing.Imaging.PixelFormat.Format32bppArgb);
                using (Drawing.Graphics graphics = Drawing.Graphics.FromImage(bitmap))
                using (Drawing2D.GraphicsPath circle = new Drawing2D.GraphicsPath())
                {
                    graphics.SmoothingMode = Drawing2D.SmoothingMode.AntiAlias;
                    graphics.InterpolationMode = Drawing2D.InterpolationMode.HighQualityBicubic;
                    graphics.Clear(Drawing.Color.Transparent);
                    circle.AddEllipse(0, 0, 32, 32);
                    graphics.SetClip(circle);
                    int side = Math.Min(source.Width, source.Height);
                    int x = (source.Width - side) / 2;
                    int y = (source.Height - side) / 2;
                    graphics.DrawImage(source, new Drawing.Rectangle(0, 0, 32, 32), x, y, side, side, Drawing.GraphicsUnit.Pixel);
                }
            }
            avatar = Drawing.Icon.FromHandle(bitmap.GetHicon());
            return avatar;
        }
    }

    public sealed class LiveToast : System.Windows.Window
    {
        private const double StackGap = 12;
        private const double ScreenEdge = 22;
        private const int GwlExStyle = -20;
        private const long WsExNoActivate = 0x08000000L;
        private static readonly List<LiveToast> ActiveToasts = new List<LiveToast>();
        private readonly System.Windows.Threading.DispatcherTimer timer;

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr", SetLastError = true)]
        private static extern IntPtr GetWindowLongPtr64(IntPtr windowHandle, int index);

        [DllImport("user32.dll", EntryPoint = "GetWindowLong", SetLastError = true)]
        private static extern int GetWindowLong32(IntPtr windowHandle, int index);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr", SetLastError = true)]
        private static extern IntPtr SetWindowLongPtr64(IntPtr windowHandle, int index, IntPtr value);

        [DllImport("user32.dll", EntryPoint = "SetWindowLong", SetLastError = true)]
        private static extern int SetWindowLong32(IntPtr windowHandle, int index, int value);

        public LiveToast(Streamer streamer, int displaySeconds)
        {
            Width = 365;
            Height = 124;
            WindowStyle = System.Windows.WindowStyle.None;
            ResizeMode = System.Windows.ResizeMode.NoResize;
            AllowsTransparency = true;
            Background = System.Windows.Media.Brushes.Transparent;
            ShowInTaskbar = false;
            Topmost = true;
            ShowActivated = false;
            Focusable = false;
            System.Windows.Controls.Border root = new System.Windows.Controls.Border();
            root.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 255, 255));
            root.CornerRadius = new System.Windows.CornerRadius(18);
            root.Padding = new System.Windows.Thickness(18);
            root.Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = System.Windows.Media.Color.FromRgb(32, 43, 66),
                BlurRadius = 20,
                ShadowDepth = 4,
                Opacity = .25
            };
            System.Windows.Controls.Grid grid = new System.Windows.Controls.Grid();
            grid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new System.Windows.GridLength(64) });
            grid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star) });
            System.Windows.Shapes.Ellipse photo = new System.Windows.Shapes.Ellipse { Width = 58, Height = 58, Fill = new SolidColorBrush(System.Windows.Media.Color.FromRgb(232, 240, 255)) };
            if (streamer.AvatarImage != null) photo.Fill = new ImageBrush(streamer.AvatarImage) { Stretch = Stretch.UniformToFill };
            grid.Children.Add(photo);
            System.Windows.Controls.StackPanel words = new System.Windows.Controls.StackPanel { VerticalAlignment = System.Windows.VerticalAlignment.Center, HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch };
            System.Windows.Controls.TextBlock title = new System.Windows.Controls.TextBlock { Text = streamer.Name + " 开播了", FontFamily = new System.Windows.Media.FontFamily("Microsoft YaHei UI"), FontSize = 17, FontWeight = System.Windows.FontWeights.Bold, Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(33, 44, 66)), TextTrimming = System.Windows.TextTrimming.CharacterEllipsis, HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch, TextAlignment = System.Windows.TextAlignment.Center };
            System.Windows.Controls.TextBlock detail = new System.Windows.Controls.TextBlock { Text = streamer.Title, FontFamily = new System.Windows.Media.FontFamily("Microsoft YaHei UI"), FontSize = 12, Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(100, 115, 140)), TextTrimming = System.Windows.TextTrimming.CharacterEllipsis, Margin = new System.Windows.Thickness(0, 6, 0, 0), HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch, TextAlignment = System.Windows.TextAlignment.Center };
            System.Windows.Controls.TextBlock openHint = new System.Windows.Controls.TextBlock { Text = "点击进入直播间", FontFamily = new System.Windows.Media.FontFamily("Microsoft YaHei UI"), FontSize = 11, FontWeight = System.Windows.FontWeights.SemiBold, Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(240, 91, 141)), Margin = new System.Windows.Thickness(0, 4, 0, 0), HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch, TextAlignment = System.Windows.TextAlignment.Center };
            words.Children.Add(title); words.Children.Add(detail); words.Children.Add(openHint);
            System.Windows.Controls.Grid.SetColumn(words, 1);
            grid.Children.Add(words);
            root.Child = grid;
            Content = root;
            timer = new System.Windows.Threading.DispatcherTimer();
            timer.Interval = TimeSpan.FromSeconds(Math.Max(3, Math.Min(3600, displaySeconds)));
            timer.Tick += delegate { Close(); };
            SourceInitialized += delegate { PreventActivation(); };
            timer.Start();
            Loaded += delegate { AddToStack(this); };
            Closed += delegate { timer.Stop(); RemoveFromStack(this); };
            MouseLeftButtonDown += delegate { OpenLiveRoom(streamer); Close(); };
        }

        private void PreventActivation()
        {
            try
            {
                IntPtr windowHandle = new WindowInteropHelper(this).Handle;
                IntPtr existingStyle = GetExtendedStyle(windowHandle);
                SetExtendedStyle(windowHandle, new IntPtr(existingStyle.ToInt64() | WsExNoActivate));
            }
            catch
            {
                // ShowActivated already keeps the normal notification path from stealing focus.
            }
        }

        private static IntPtr GetExtendedStyle(IntPtr windowHandle)
        {
            return IntPtr.Size == 8
                ? GetWindowLongPtr64(windowHandle, GwlExStyle)
                : new IntPtr(GetWindowLong32(windowHandle, GwlExStyle));
        }

        private static void SetExtendedStyle(IntPtr windowHandle, IntPtr value)
        {
            if (IntPtr.Size == 8)
            {
                SetWindowLongPtr64(windowHandle, GwlExStyle, value);
            }
            else
            {
                SetWindowLong32(windowHandle, GwlExStyle, value.ToInt32());
            }
        }

        private static void AddToStack(LiveToast toast)
        {
            ActiveToasts.Remove(toast);
            ActiveToasts.Add(toast);
            ArrangeStack();
        }

        private static void RemoveFromStack(LiveToast toast)
        {
            ActiveToasts.Remove(toast);
            ArrangeStack();
        }

        private static void ArrangeStack()
        {
            System.Windows.Rect area = System.Windows.SystemParameters.WorkArea;
            for (int i = 0; i < ActiveToasts.Count; i++)
            {
                LiveToast toast = ActiveToasts[i];
                toast.Left = area.Right - toast.Width - ScreenEdge;
                toast.Top = area.Bottom - toast.Height - ScreenEdge - i * (toast.Height + StackGap);
            }
        }

        private static void OpenLiveRoom(Streamer streamer)
        {
            if (streamer == null) return;
            string url = String.IsNullOrWhiteSpace(streamer.Url)
                ? (streamer.Platform == "douyu" ? "https://www.douyu.com/" : "https://live.bilibili.com/") + streamer.RoomId
                : streamer.Url;
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
        }
    }
}
