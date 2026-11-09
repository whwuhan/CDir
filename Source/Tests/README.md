# 文件系统、语言与版本控制回归测试

测试代码统一放在 `Source/Tests`：`Program.cs`、各后台测试文件由独立的 `CDirTests.csproj` 编译；各 `*SmokeTests.cs` 是 `CDir.UI.ExplorerTab` 的部分类，由主项目编译，通过 `--smoke-test` 入口运行。构建、测试和打包脚本统一位于 `Source/Scripts`。

测试进程不注册为用户实例，`-Published` 即使使用日常 CDir 的同一路径，也不会被用户窗口计为另一个实例或暂停其自动保存。测试不得发现或克隆用户窗口；配置存储拒绝访问自有测试范围以外的路径及文件链接。修改测试隔离或持久化时，组合运行 `-UIGroup lifecycle -ScriptGroup release -PublishConfiguration`；发布脚本检查覆盖实时工作区、多标签及未保存类别的保留、旧配置备份、无效快照和不匹配进程路径的拒绝。

发布直接使用 `Source/Scripts/Build.ps1 -Publish`。脚本先核实正在运行实例的完整程序路径，通过现有工作区管道备份所有实时快照及磁盘配置；成功后才停止实例，按最近活跃窗口恢复配置，再构建和打包。无法捕获完整快照时，不得强制结束实例。每个实例的备份保留在 `Build/PublicationWorkspaces`；发布成功后仅恢复原先运行的程序，不为测试启动日常窗口。

内嵌终端使用 `Source/Scripts/Test.ps1 -UIGroup terminal` 验证；与快捷键同时修改时合并选择 `-UIGroup terminal,shortcuts`。终端组只在自有目录内运行真实 PowerShell/ConPTY，覆盖首次工作目录、Unicode/ANSI、隐藏后继续执行、恢复原会话、缩放、语言切换、退出重启及大量输出时的进程清理，不发送全局按键或移动桌面指针。终端渲染依赖固定版本的 VirtualTerminal.WPF，其许可证随发布包保存在 `ThirdParty/VirtualTerminal.LICENSE.txt`。

终端组还在 PowerShell 已输出提示符后显式创建无障碍接口，检查文本模式和提示符读取，并覆盖 File 菜单打开与关闭后的焦点恢复。首次打开、隐藏恢复和重启后的输入检查通过终端控件的 WPF 文本/按键路由发送文字、退格和回车，以验证控件到真实 Shell 的完整输入路径。CDir 使用本地无障碍接口适配，初始化时只读取快照，注册完成后才发送文本变化通知，避免上游接口构造时 `provider` 为空打断焦点和菜单操作。

日常验证从项目根目录调用测试入口，只选本次改动相关的组：

```powershell
.\Source\Scripts\Test.ps1 -Group address
.\Source\Scripts\Test.ps1 -Group language
.\Source\Scripts\Test.ps1 -Group filesystem
```

`Source/Scripts/Build.ps1` 默认仅编译，不运行测试或发布。`Source/Scripts/Test.ps1` 串行启动所选测试，使用 Windows Job Object 管理自己创建的进程树，并降低磁盘 I/O 与后台调度优先级。按用户偏好，默认 CPU 不限、不绑单核、不强制缩减运行时并行度；只有显式传入 `-CpuPercent 1..100` 才设置 CPU 上限，默认 `0` 表示不限。测试仅在独立的临时目录中生成数据，不展开拥挤的系统临时目录树。直接启动后台测试程序也会降为 Idle/后台 I/O 模式；日常仍应使用 `Test.ps1` 以获得进程树清理、超时控制与分组报告。

同一次改动涉及多个组时，在一次调用中合并选择，每个项目只构建一次。界面组始终串行运行，每组使用独立应用实例和自有目录，`-UIGroup all` 同样展开为全部独立组，避免上一组的焦点、导航或临时状态影响下一组。测试入口保留增量构建；仅在当前源码和配置的二进制已经构建完成、重跑期间未修改源码时，使用 `-SkipBuild`。输出列出已选择、未选择、实际完成和跳过的组，并记录每个进程的耗时（含队列等待）与整次测试耗时。`all` 与具体组同时选择时，不会重复执行覆盖的组。子进程使用正常内存页优先级，不重复进入压缩工作集的后台内存模式，保留很低的 I/O 与 Idle CPU 调度。

```powershell
# 一次构建，验证相关的后台逻辑与界面
.\Source\Scripts\Test.ps1 -Group address -UIGroup navigation,address
# 已构建且源码未变时，快速重跑单组
.\Source\Scripts\Test.ps1 -UIGroup address -SkipBuild
# 仅检查文件操作与命名对话框，不必执行全套界面测试
.\Source\Scripts\Test.ps1 -UIGroup operations
# 使用自包含 win-x64 发布配置完整重建并检查，避免 WPF 界面资源与连接代码缓存错配
.\Source\Scripts\Test.ps1 -UIGroup navigation -PublishConfiguration
# 发布后检查实际发布目录中的二进制，不构建、不覆盖发布文件
.\Source\Scripts\Test.ps1 -UIGroup navigation -Published
```

后台组为 `filesystem`（29 项）、`language`（4 项）、`address`（4 项）、`version-control`（19 项）。不指定组时仅执行前三组，共 37 项；只有明确选择 `version-control` 或 `all` 才创建 Git/SVN 测试仓库。后台可执行程序也支持 `--group address`、重复 `--group` 或逗号分隔组名，`--group all` 表示全部 56 项后台检查。输出会注明实际运行的组及本次通过、失败、跳过数量，未执行的组不计为通过；测试失败返回 1，参数错误返回 2。

```powershell
# 按需执行较重的版本控制回归
.\Source\Scripts\Test.ps1 -Group version-control
# 全部后台回归，不会自动打开界面
.\Source\Scripts\Test.ps1 -Group all
# 只有明确选择 UIGroup 才执行真实 WPF 界面检查
.\Source\Scripts\Test.ps1 -UIGroup address
.\Source\Scripts\Test.ps1 -UIGroup all
```

界面检查默认不执行；通过 `-UIGroup` 显式选择后，直接在当前 Windows 桌面运行，使用软件渲染。独立桌面的创建、启动选项和校验均已删除；测试窗口、菜单和对话框可能出现在当前桌面，主窗口保持 `ShowActivated=false`、不在任务栏显示，不主动激活。`-UIGroup` 单独使用时只运行界面组，不附带默认后台组。界面组包括 `tabs`、`navigation`、`sidebar`、`address`、`operations`、`columns`、`fonts`、`search`、`language`、`shell-icons`、`version-control`、`all`；应用默认的烟测组为轻量 `navigation`，仍要求启动器标记，直接裸启动会在创建窗口前被拒绝。完整界面回归必须明确选择 `-UIGroup all`。

测试不依赖第三方测试框架。产生的文件仅写入带随机 ID 和所有权标记的专用测试目录，结束后清理。不会打开用户文件或把文件放入回收站。operations 中的工作目录回归仅隐藏运行本次自有目录内生成的无害 .bat/.cmd 脚本，核对相对路径读取结果后等待其退出；不会激活控制台，也不会运行用户脚本。测试入口的超时和取消只终止自己建立的进程树，不关闭用户正常使用的 CDir 或 IDE。

界面报告、截图和启动/分组进度日志保存在 `Artifacts/TestRuns/<本次ID>/`。启动慢或超时时，先查看对应的 `*.png.progress.log` 和 `*.error.txt`，定位到窗口构造、初始导航或具体测试组，再只重跑相关组；失败时保留本次临时数据便于诊断。

覆盖路径标准化、Unicode/空格/环境变量/相对路径、目录与隐藏项枚举、元数据、创建和重命名、复制和移动、同名冲突与批次预检保护、递归目标保护及取消。还验证文件复制保留隐藏/只读属性、修改时间和 NTFS `Zone.Identifier` 附加流。链接测试先尝试临时符号链接；权限不足时，使用隐藏的 PowerShell 进程创建临时目录联接点。两者都无法创建时明确显示 `SKIP`。

语言回归直接检查四个嵌入 JSON 的键、非空值与格式占位符，避免默认语言回退掩盖漏译；同时检查不支持语言的回退、缓存文件元数据刷新和文件系统错误翻译。

表头和偏好设置分类可通过 `Source/Scripts/Test.ps1 -UIGroup columns` 单独验证。该组检查原生表头拖动已启用、显示顺序与隐藏状态保存回读、旧配置兼容、无效配置回退、至少保留一列、搜索绝对路径列的独立控制，以及列变化不触发重新搜索。偏好设置检查通过 General、UI 两个分类操作真实控件，并生成 `PreferencesUI-<语言>.png` 和 `ColumnsReordered.png` 截图。

地址栏分段模型增加 4 项后台回归，验证驱动器根、UNC 共享根、中文与空格名称、尾分隔符和路径标准化，并拒绝相对路径；分段不访问文件系统或网络共享。地址栏增加 8 项界面检查，验证点击真实路径段导航、后退/前进同步、编辑快捷键、Enter/Esc、失败路径、过长路径菜单，以及后台刷新时保留编辑草稿。

显式选择 `language` 界面组时，四语用例通过真实语言选择器切换，验证界面资源、文件类型、常用位置、导航状态、筛选、排序、选中项目及右键菜单；验证本地化名称对话框的确认/取消，以及专用临时文件中的真实设置保存和回读。检查不会改写用户的设置文件。提供截图路径时，还会在同一输出目录生成 `LanguageChinese.png`、`LanguageJapanese.png`、`LanguageKorean.png`、`LanguageEnglish.png`。

系统图标增加 6 项界面检查：文件夹、磁盘、文本文件、可执行文件和快捷方式返回冻结位图且保留透明度；独立调用 Windows Shell 对比像素，验证不同程序及快捷方式的图标；并发请求复用缓存并遵守缓存和队列边界；两轮各 24 个未缓存请求不持续积累原生 GUI 句柄；文件列表保留 28 DIP 行高和 16 DIP 图标；控件快速重绑时不显示旧结果，空路径占位保持隐藏。测试不会执行临时程序或快捷方式。参考调用在释放 HICON 和结束 COM 后才完成任务；清理前先离开测试目录、等待图标队列结束，再核对临时路径与所有权标记并进行有限重试。若断言和清理同时失败，会保留两项错误。

原生图标对比使用不带第三方状态叠加层的 Shell 基础图标，并保留快捷方式箭头，防止 Git/SVN 等扩展的缓存标记被当成程序确认的修改状态。

侧栏组覆盖类别菜单、删除确认的确认/取消/关闭、目录递归展开与收起、收藏入口添加与排序、跨类别拖动及拖出移除。目录树指示线通过真实渲染验证：磁盘根节点不绘制传入线，展开分支（包括最后一个同级节点）的竖线延伸到子树底部，折叠的末尾节点在标题中点收尾；同时检查深层滚动、不同字号、150% 渲染、收藏子目录及容器重新挂载。截图包括 `TreeGuides.png`、`TreeGuidesDeepScrolled.png`、`TreeGuidesBookmark.png`。使用 `Source/Scripts/Test.ps1 -UIGroup sidebar` 单独运行。

后台语言组包含英文大小写规则检查：功能性菜单、按钮和字段标题使用 Title Case，普通说明、提示、错误和状态消息使用句式大小写；同时保护品牌、缩略词、快捷键、格式占位符和用户名称。删除确认框的界面检查还要求 Cancel 为高亮和默认操作，OK 使用普通样式。

版本控制包含 19 项后台回归，创建隔离的真实 Git 仓库、worktree、子模块和本地 SVN 仓库。验证未修改、仅改时间戳、内容/属性修改、暂存、新增、中文与空格重命名、删除、忽略、未跟踪、真实合并/更新冲突、提交/还原后刷新、嵌套工作区边界、工具缺失、超时、取消和不完整输出。另覆盖 SVN XML 路径规范化后的缓存失效、可见目录浅查询与快照合并、Git/SVN 双向嵌套及普通目录无状态、非标准安装目录的工具查找。命令仅作用于测试目录，Git 身份和空 hooks 目录通过单次命令参数传入，不改用户全局配置；SVN 通过 file:// 访问测试仓库并使用独立配置目录。工具缺失时对应后台用例明确显示 SKIP。

版本控制包含 21 项界面检查，覆盖状态绑定、绘制、深层文件变化、提交/还原及导航并发，用实际 Git 命令计数验证静止超过原轮询周期仍不重查、临时 index.lock 不触发重查、同仓库导航复用快照。另验证 SVN 内容修改后自动刷新、进入子目录识别中文文件状态，以及离开仓库后列表、目录树、地址栏均无徽标。后台另有缓存用例验证内容变化只启动一次 status 查询、复用索引清单，以及撤掉祖先监视后返回仓库会重新核实。自定义类别另覆盖根目录、独立子目录与重复收藏的 Git/SVN 状态绑定，主面板位于普通目录时的仓库发现、文件修改与还原后的自动刷新、待刷新状态，以及类别折叠后停止查询和监视、重新展开后恢复状态。此组需要 git、svn、svnadmin；截图输出 VersionControlGit.png、VersionControlSvn.png 和 VersionControlCategories.png。原生对象在所有权检查后清理，不遍历重解析点。

此前 Windows 11 x64 的完整验证基线为 51 项后台、92 项界面检查通过（Git 2.55.0.windows.5、SVN 1.14.5）。这是历史全量结果，不代表每次按组验证都重新执行了这些检查；本次结果以测试入口输出的所选组报告为准。测试不代替 Windows 10 实机、跨卷移动、网络共享及 Windows 回收站交互验证。

字号和紧凑行距使用 `Source/Scripts/Test.ps1 -UIGroup fonts`：覆盖旧设置默认值、八个真实字号控件、独立生效、最大字号布局、保存回读、无效值处理、字号变化不重启搜索，以及紧凑行距下的网格线对齐。截图包括 FontsCompact.png、FontsLarge.png 和 PreferencesUI-en-US.png。

多标签与后台删除专项：`Source/Scripts/Test.ps1 -UIGroup tabs`。覆盖整页布局、独立导航/搜索/侧栏/选择/状态、独立终端、切换/关闭与资源释放、标准标签快捷键，以及原生 Delete 菜单在 STA 工作线程运行时导航、搜索和标签切换保持可用。删除测试使用精确限定的自有文件和文件夹，不修改用户回收站；取消与失败同样检查恢复。

发布版本规则使用 `Source/Scripts/Test.ps1 -ScriptGroup release` 验证；与标题显示一起验证时合并 `-UIGroup fonts -PublishConfiguration`。大版本仅由用户指定；致命修复使用 `-FatalBugFix`，用户明确要求大功能提升中版本时使用 `-FeatureRelease`，测试与发布须使用相同选项。小版本仅在源码或配置变动后的成功发布中递增，同一源码重复发布或打包不递增；两种中版本选项同时选择也只加一次，失败和测试不消耗版本号。

外部文件管理器拖入主面板使用 transfer 界面组：通过不允许读取 CDir 私有格式的数据对象验证混合文件/目录在空白区及文件夹行的 DragEnter、DragOver 和 Drop 路由，按偏好设置复制或移动，兼容 FileNameW 转换，并验证同目录复制生成系统命名副本、拒绝同目录移动及纯文本。使用真实命中测试选择空白区，保持鼠标和全局键盘不动。

回收站拖放在 transfer 组创建短路径的自有输入，使用原生删除回调获取仅该测试项目的标识，再按确切路径绑定回收站命名空间，验证真实复制/移动、原名、嵌套内容、复制保留来源、移动移除来源、CIDA 相对/绝对标识、错误数据拦截和取消结果。仅操作创建前验证所有权的测试文件夹；不枚举用户回收站，不写入系统撤销历史，不执行全局撤销。复制用例结束后由 Windows 按已捕获标识清理自有回收站项目；短路径根带运行所有权标记，结束后验证并删除。sidebar 组验证 CDir 回收站视图可拖出原生 Shell 标识。
Windows 自带回收站回归必须通过其 IShellFolder.GetUIObjectOf 获取原生 COM IDataObject，并使用 GetAttributesOf 返回的真实 Move-only 能力进行 DragEnter/DragOver/Drop 协商；不得用自行构造的 Copy|Move 掩码替代来源能力。覆盖文件、嵌套文件夹、混合批次、复制选项的两种状态、真实移动及 COM SetData 的完成回执、取消保留来源，以及普通/混合非回收站标识不得在复制模式被降级为移动。旧的纯 CIDA 用例仅验证序列化协议，不代表 Windows 资源管理器的拖出协商。
分割窗格使用 `-UIGroup panes`：覆盖四向范围预览、拖放取消、中心与过窄目标拒绝、完整窗格、窄窗口可用性、嵌套布局、跨窗格标签移动及空窗格合并、活动窗格快捷键、置顶状态、分隔比例保存、重启恢复与损坏布局回退。与标签或持久化同时修改时组合选择 `panes,tabs,lifecycle,shortcuts,address,sidebar` 和 `-ScriptGroup release -PublishConfiguration`；发布后仅需 `-UIGroup panes -Published` 捕获当前版本预览。

表头自适应使用 `-UIGroup columns,shortcuts`，覆盖所有当前显示列、虚拟化屏幕外内容、当前筛选结果、字体与单 Tab 隔离、右键菜单、自定义/清除快捷键和窄列恢复。`panes` 同时验证 Alt+R 按叶窗格数量均分嵌套区域、最小尺寸保护、自定义绑定和持久化。Alt+Q 默认迁移通过 shortcuts 和 lifecycle 组验证；个人 Alt+1 重绑定在迁移后保留。
