namespace ScreenGuard.Core;

/// <summary>
/// 编程类软件识别。
///
/// 设计原则（硬性要求）：任何编程类软件都不得被终止、不得被最小化、不得被干扰。
/// 判定采用三层匹配，任一命中即受保护：
///   1. 进程名精确匹配（IDE、编辑器、终端、构建工具、运行时）
///   2. 进程名特征匹配（如 xxx64 / xxx64.exe 之类的变体）
///   3. 可执行文件路径片段匹配（IDE 常见的安装目录）
/// </summary>
public static class DevToolDetector
{
    /// <summary>编程类进程名（不区分大小写，不含 .exe 后缀）。</summary>
    private static readonly HashSet<string> DevProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        // —— 华为 / 鸿蒙 ——
        "deveco", "deveco-code", "devecostudio", "deveco-studio",
        "hdc", "ohpm", "hvigor", "cbuild", "hars", "hmscore",
        "ide", "idea", "idea64", "intellijidea", "intellijidea64",

        // —— JetBrains 系 ——
        "clion", "clion64", "pycharm", "pycharm64", "webstorm", "webstorm64",
        "goland", "goland64", "rider", "rider64", "rubymine", "rubymine64",
        "phpstorm", "phpstorm64", "datagrip", "datagrip64", "rustrover", "fleet",

        // —— VS / VS Code 系 ——
        "devenv", "code", "code-insiders", "vscodium", "codium", "cursor",
        "trae", "windsurf", "zed", "brackets", "atom", "sublime_text",
        "sublime_text3", "notepad++", "hbuilder", "hbuilderx", "visualstudio",
        "vs_installer", "vs_setup_bootstrapper", "servicehub", "vs_servicehub",
        "vscode", "vs_installer", "live_share", "omnisharp", "omnisharp",
        "razor", "dotnet-format", "msbuild", "vbcscompiler", "vsixinstaller",

        // —— 其他 IDE / 编辑器 ——
        "eclipse", "netbeans", "netbeans64", "studio3", "studio64", "androidstudio",
        "qtcreator", "kate", "gedit", "lazarus", "fpide", "cutter", "sakura",
        "textpad", "editpad", "notepad2", "bbedit", "textmate", "nova",
        "androidstudio64", "emacs", "neovim", "nvim", "vim", "gvim", "vi",
        "micro", "helix", "hx", "kakoune", "lite-xl", "xi", "geany", "bluefish",
        "monaco", "posh", "powershell_ise", "notepad",

        // —— 终端 / Shell / 控制台 ——
        "cmd", "powershell", "pwsh", "conhost", "windowsterminal", "wt",
        "bash", "wsl", "wslhost", "wslservice", "vmmem", "vmmemwsl",
        "alacritty", "wezterm", "kitty", "hyper", "terminator", "tilix",
        "guake", "yakuake", "cmder", "mintty", "cygwin", "msys", "busybox",
        "git-bash", "iterm", "tabby", "wezterm-gui", "shell", "sh", "zsh",

        // —— 构建工具 / 编译 / 打包 ——
        "maven", "mvn", "mvnw", "gradle", "gradlew", "gradle-daemon",
        "sbt", "mill", "bazel", "bazelisk", "buck", "ninja", "cmake", "make",
        "mingw32-make", "msbuild", "xbuild", "nmake", "waf", "scons",
        "webpack", "vite", "rollup", "esbuild", "parcel", "snowpack",
        "tsc", "eslint", "prettier", "biome", "stylelint", "postcss",
        "gulp", "grunt", "browserify", "jspm", "rspack", "turbo", "nx", "lerna",

        // —— 包管理 / 依赖 ——
        "npm", "npx", "yarn", "pnpm", "bun", "bunx", "corepack", "conda",
        "pip", "pip3", "poetry", "uv", "pipenv", "pipx", "pyenv", "homebrew",

        // —— 运行时 / 解释器 ——
        "node", "nodejs", "deno", "bun", "python", "python3", "pythonw", "py",
        "ruby", "irb", "php", "perl", "java", "javaw", "jdk", "jre", "scala",
        "kotlin", "kotlinc", "groovy", "dotnet", "dotnetfx", "mono", "csc",
        "vbc", "fsharp", "swift", "swiftc", "clang", "clangd", "clang-cl",
        "gcc", "g++", "cc", "c++", "ld", "link", "cl", "icc", "icx",
        "cargo", "rustc", "rustup", "go", "gopls", "tinygo", "nim", "nimble",
        "zig", "crystal", "elixir", "iex", "erl", "escript", "dlang", "ldc2",
        "gdc", "fpc", "pascal", "delphi", "dcc32", "fortran", "gfortran",
        "julia", "julia-1", "r", "rscript", "matlab", "octave", "gnuplot",
        "lua", "luajit", "tcl", "wish", "prolog", "swipl", "scheme", "guile",

        // —— 数据库 / 容器 / 运维 ——
        "docker", "dockerd", "docker-compose", "com.docker.backend", "com.docker.proxy",
        "podman", "kubectl", "helm", "k9s", "minikube", "kind",
        "postgres", "postgresql", "mysqld", "mariadbd", "mongod", "redis-server",
        "nginx", "httpd", "apache", "tomcat", "elasticsearch", "rabbitmq-server",
        "zookeeper", "kafka-server", "etcd", "consul", "vault",
        "dbeaver", "navicat", "pgadmin", "datagrip", "sqlbrowser",
        "mongodb", "redis-cli", "mysqlsh", "sqlite3",

        // —— 版本控制 / CI ——
        "git", "git-lfs", "gitk", "git-gui", "tortoisegitproc", "svn", "svnserve",
        "hg", "gh", "glab", "jenkins", "gitlab-runner", "circleci",

        // —— 嵌入式 / 物联网 / 逆向 ——
        "openocd", "jlink", "pyocd", "st-info", "stlink", "wokwi",
        "ida", "ida64", "idaq", "idaq64", "ghidra", "ghidraRun", "x64dbg", "x32dbg",
        "windbg", "dnspy", "ilspycmd", "rider64", "immunity", "radare2",
        "frida", "charles", "fiddler", "wireshark", "tshark",

        // —— 辅助工具 ——
        "editor_sdk", "editor", "language_server", "omnisharp-roslyn",
        "omnisharp-msbuild", "dotnet-watch", "hotreload", "vstest",
        "testhost", "nunit", "xunit", "mstest", "junit", "pytest",

        // —— 图形 / 工具链 ——
        "cmake-gui", "kdevelop", "plasma", "code-oss", "vscode-server",
        "codespaces", "gitpod", "theia", "che", "eclipse-jdt",
    };

    /// <summary>
    /// 可执行文件路径片段 —— 命中即视为编程类软件。
    /// 注意：片段必须足够特异。像 "\sdk\" 这种宽泛规则会误伤大量程序
    /// （实测曾把华为模拟器 Emulator 误判为编程软件），
    /// 因此只保留指向明确开发工具安装目录的片段。
    /// </summary>
    private static readonly string[] DevPathHints =
    {
        "jetbrains", "intellij", "pycharm", "webstorm", "clion", "goland",
        "rider", "rubymine", "phpstorm", "datagrip", "fleet",
        "microsoft visual studio", "\\vscode\\", "\\vscodium\\", "\\deveco\\",
        "android studio", "androidstudio", "\\eclipse\\", "\\netbeans\\",
        "\\sublime text\\", "\\notepad++\\", "\\hbuilder\\", "\\hbuilderx\\",
        "\\nodejs\\", "\\python3", "\\jdk\\", "\\golang\\", "\\rust\\",
        "\\docker\\", "\\kubernetes\\", "\\vs_installer\\",
        "openharmony", "hongmeng", "deveco-code", "@deveco",
    };

    /// <summary>判断进程名是否为编程类。</summary>
    public static bool IsDevProcessName(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName)) return false;

        var name = processName.Trim();
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            name = name[..^4];

        if (DevProcessNames.Contains(name)) return true;

        // 变体匹配：xxx64 / xxx-x64 / xxx_x64 等
        var stem = name;
        foreach (var suffix in new[] { "64", "x64", "-x64", "_x64", "arm64" })
        {
            if (stem.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                stem = stem[..^suffix.Length];
                break;
            }
        }
        if (stem != name && DevProcessNames.Contains(stem)) return true;

        return false;
    }

    /// <summary>判断进程路径是否指向编程类软件。</summary>
    public static bool IsDevPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        foreach (var hint in DevPathHints)
        {
            if (path.Contains(hint, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>综合判断：进程名或路径命中即为编程类软件。</summary>
    public static bool IsDevProcess(string? processName, string? exePath = null)
        => IsDevProcessName(processName) || IsDevPath(exePath);

    /// <summary>供配置界面展示的可编辑名单。</summary>
    public static IReadOnlyCollection<string> KnownNames => DevProcessNames;
}
