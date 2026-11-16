using System;
using System.Collections.Generic;

namespace PerfWidget;

/// <summary>
/// How Linux processes roll up into apps. Names are lower-case comm, un-truncated where possible
/// (see <see cref="LinuxParse.PickName"/>); every name longer than 15 bytes is also added in its cut form.
/// </summary>
internal static class LinuxRules
{
    public static GroupingRules Build()
    {
        var r = new GroupingRules
        {
            // OS launchers: their children start new apps.
            // kthreadd is deliberately NOT here: kernel threads are its children, and they must stay under it so the
            // "kthreadd" alias folds hundreds of kworkers into one "Linux kernel threads" group.
            Boundary = new()
            {
                // init and service managers (pid 1 in containers too)
                "init", "systemd", "tini", "dumb-init", "docker-init", "catatonit", "s6-svscan", "s6-supervise",
                "runsvdir", "runsv", "supervisord", "openrc-init",
                // sessions and privilege changes
                "login", "sshd", "sshd-session", "sshd-auth", "sudo", "su", "doas", "pkexec", "cron", "crond", "anacron", "atd",
                "dbus-daemon", "flatpak-session-helper", "xdg-desktop-portal",
                // container monitors: each container's process tree is its own app
                "containerd-shim-runc-v2", "containerd-shim", "conmon",
                // display managers, sessions, shells, compositors
                "gdm", "gdm3", "gdm-session-worker", "gdm-wayland-session", "gdm-x-session", "sddm", "sddm-helper", "lightdm", "greetd",
                "gnome-shell", "gnome-session-binary", "plasmashell", "kwin_wayland", "kwin_wayland_wrapper", "kwin_x11", "ksmserver",
                "krunner", "startplasma-wayland", "startplasma-x11", "plasma_session", "xorg", "xwayland", "xinit",
                "xfce4-session", "xfce4-panel", "xfdesktop", "mate-session", "mate-panel", "cinnamon", "cinnamon-session",
                "cinnamon-launcher", "lxqt-session", "lxqt-panel", "budgie-panel", "budgie-wm", "cosmic-comp", "cosmic-session",
                "cosmic-launcher", "cosmic-panel", "sway", "hyprland", "river", "labwc", "wayfire", "niri", "weston",
                "i3", "awesome", "openbox", "bspwm", "xmonad", "herbstluftwm", "dwm", "rofi", "wofi", "fuzzel", "dmenu",
                // terminals
                "gnome-terminal-server", "kgx", "ptyxis", "ptyxis-agent", "konsole", "yakuake", "kitty", "alacritty",
                "wezterm-gui", "wezterm-mux-server", "xterm", "uxterm", "urxvt", "rxvt", "st", "tilix", "terminator", "foot",
                "ghostty", "xfce4-terminal", "mate-terminal", "lxterminal", "qterminal", "terminology", "guake", "tilda",
                // multiplexers
                "tmux: server", "tmux", "screen", "zellij",
            },
            Shells = new() { "bash", "zsh", "fish", "sh", "dash", "nu", "ash", "ksh", "mksh", "tcsh", "csh", "pwsh", "elvish", "xonsh" },
            PrefixAliases = new[]
            {
                ("unrealeditor", "Unreal Engine"), ("ue4editor", "Unreal Engine"), ("shadercompile", "Unreal Engine"),
                ("blender", "Blender"), ("godot", "Godot"), ("containerd-shim", "Docker"), ("qemu-system-", "QEMU VM"),
                ("gimp-", "GIMP"), ("perfwidget", "PerfWidget (this monitor)"),
            },
            GenericHosts = new()
            {
                "node", "nodejs", "python", "python2", "python2.7", "python3", "pypy", "pypy3", "java", "ruby", "perl", "php",
                "bun", "deno", "electron", "dotnet", "mono", "lua", "luajit",
            },
        };
        for (int v = 6; v <= 15; v++) r.GenericHosts.Add("python3." + v);

        var d = r.Aliases;
        void A(string name, params string[] names) { foreach (var n in names) d[n] = name; }

        A("Linux kernel threads", "kthreadd");

        // browsers
        A("Chrome", "chrome", "google-chrome", "google-chrome-stable", "chromium", "chromium-browser");
        A("Firefox", "firefox", "firefox-bin", "firefox-esr", "isolated web co", "isolated web content", "web content",
          "webextensions", "rdd process", "socket process", "utility process", "privileged cont", "privileged content",
          "gpu process", "isolated servic", "file:// content", "forkserver");
        A("Edge", "msedge", "microsoft-edge", "microsoft-edge-stable");
        A("Brave", "brave", "brave-browser");
        A("Vivaldi", "vivaldi", "vivaldi-bin");
        A("Opera", "opera");
        A("Thunderbird", "thunderbird", "thunderbird-bin");

        // editors and IDEs
        A("VS Code", "code", "code-insiders", "code-oss", "codium", "vscodium");
        A("Cursor", "cursor");
        A("Windsurf", "windsurf");
        A("Zed", "zed", "zed-editor", "zeditor");
        A("IntelliJ IDEA", "idea"); A("PyCharm", "pycharm"); A("CLion", "clion"); A("WebStorm", "webstorm");
        A("GoLand", "goland"); A("Rider", "rider"); A("RustRover", "rustrover"); A("PhpStorm", "phpstorm");
        A("JetBrains Toolbox", "jetbrains-toolbox");

        // creative and games
        A("Blender", "blender", "blender-launcher");
        A("Unreal Engine", "unrealeditor", "unrealeditor-cmd", "ue4editor", "shadercompileworker", "unrealbuildtool",
          "unrealtraceserver", "crashreportclienteditor", "unrealinsights", "zenserver");
        A("Godot", "godot");
        A("Unity", "unity", "unityhub", "unityhub-bin");
        A("Steam", "steam", "steamwebhelper");
        A("OBS Studio", "obs", "obs-ffmpeg-mux");
        A("GIMP", "gimp"); A("Krita", "krita"); A("Inkscape", "inkscape"); A("Kdenlive", "kdenlive");
        A("DaVinci Resolve", "resolve");
        A("LibreOffice", "soffice.bin", "soffice", "oosplash", "libreoffice");

        // chat and media
        A("Discord", "discord", "discordcanary", "discordptb");
        A("Slack", "slack");
        A("Spotify", "spotify");
        A("Zoom", "zoom", "zoomwebviewhost");
        A("Teams", "teams", "teams-for-linux");
        A("Telegram", "telegram-desktop");
        A("Signal", "signal-desktop");
        A("Obsidian", "obsidian");

        // servers, containers, VMs, AI
        A("Docker", "dockerd", "containerd", "containerd-shim", "containerd-shim-runc-v2", "docker-proxy", "docker");
        A("Podman", "podman", "conmon");
        A("Ollama", "ollama", "ollama_llama_server");
        A("LM Studio", "lm studio", "lm-studio");
        A("PostgreSQL", "postgres", "postmaster");
        A("MySQL", "mysqld", "mariadbd");
        A("Redis", "redis-server");
        A("VirtualBox", "virtualbox", "virtualboxvm", "vboxheadless", "vboxsvc");
        A("QEMU VM", "qemu-kvm");
        A("VMware", "vmware-vmx");

        // desktop and system services
        A("Display server", "xorg", "xwayland");
        A("GNOME Shell", "gnome-shell");
        A("KDE Plasma", "plasmashell");
        A("KWin", "kwin_wayland", "kwin_wayland_wrapper", "kwin_x11");
        A("Search indexing", "tracker-miner-fs-3", "tracker-miner-fs", "tracker-extract-3", "tracker-extract",
          "localsearch-3", "localsearch", "localsearch-extractor-3", "baloo_file", "baloo_file_extractor");
        A("Package updates", "packagekitd", "apt", "apt-get", "aptitude", "dpkg", "unattended-upgr", "unattended-upgrade",
          "unattended-upgrades", "dnf", "dnf5", "yum", "rpm", "zypper", "pacman", "pamac-daemon", "update-notifier");
        A("Snap daemon", "snapd");
        A("Firmware updates", "fwupd");
        A("Software center", "gnome-software", "plasma-discover");
        A("Audio server", "pipewire", "pipewire-pulse", "wireplumber", "pulseaudio");
        A("System log", "systemd-journald");
        A("Wine server", "wineserver");
        A("Dropbox", "dropbox"); A("Nextcloud", "nextcloud"); A("Syncthing", "syncthing");

        // terminals (the shells inside them are separate apps)
        A("GNOME Terminal", "gnome-terminal-server");
        A("GNOME Console", "kgx");
        A("Ptyxis", "ptyxis", "ptyxis-agent");
        A("Konsole", "konsole");
        A("WezTerm", "wezterm-gui", "wezterm-mux-server");
        A("Alacritty", "alacritty"); A("Ghostty", "ghostty"); A("Tilix", "tilix"); A("Terminator", "terminator");

        A("PerfWidget (this monitor)", "perfwidget", "perfwidget.app");

        AddCut(r.Boundary);
        AddCut(r.Shells);
        AddCut(r.GenericHosts);
        foreach (var (k, v) in new List<KeyValuePair<string, string>>(d))
            if (k.Length > LinuxParse.CommMax) d.TryAdd(k.Substring(0, LinuxParse.CommMax), v);

        IReadOnlyDictionary<string, string> aliases = d;
        r.AppFromPath = path => LinuxParse.AppName(path, aliases);
        return r;
    }

    /// <summary>Also match the 15-byte comm the kernel keeps, for when the full name can't be recovered.</summary>
    static void AddCut(HashSet<string> set)
    {
        foreach (var n in new List<string>(set))
            if (n.Length > LinuxParse.CommMax) set.Add(n.Substring(0, LinuxParse.CommMax));
    }
}
