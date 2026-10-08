# 本地验证辅助程序

这些控制台程序不随运行包分发，不参与主项目构建。

使用 .NET Framework 的 csc.exe 编译 x64 exe，引用构建出的开播铃.exe、
PresentationCore.dll、PresentationFramework.dll、WindowsBase.dll、System.Xaml.dll。
PreviewValidation 另需引用 System.Web.Extensions.dll。

将测试 exe、开播铃.exe 与 Assets 放到独立可写测试目录中；测试目录的 Data
只能使用副本或临时数据，不要直接在个人软件目录中运行测试。

MemoryProbe 参数：`policy <App.xaml路径> [检查轮数]`。测试目录的 Data 可放关注数据副本。
`policy` 验证当前默认绘制策略；`software` 强制软件绘制；`baseline` 使用 WPF 默认绘制，
便于搭配原版应用程序集比较。每轮间隔等待 6 秒，不发送开播提醒或音效。

PreviewValidation 参数：`<测试输出目录> <样本Data目录> <App.xaml路径>`。
样本目录须包含至少三位主播的开播铃数据.json 及对应 avatars 缓存；程序只读取样本，
在测试 exe 所在目录的 Data 下写入临时验证数据，并输出示例状态的界面截图。
它还播放两段自己生成的静音 WAV 验证音效队列，不播放个人音效。

两个程序均使用独立的普通 WPF Application，不启动真实 App 的单实例入口，
不会激活或退出已有软件；窗口放在屏幕外，测试时可能短暂显示独立托盘图标。
请把编译器 TEMP/TMP 指向有可用空间的目录，不要为此清空个人数据。
