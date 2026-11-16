using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using FrameForge.Core;

namespace FrameForge.Pages;

/// <summary>
/// O lado visível das atualizações: verifica no arranque e de 6 em 6 horas; na 1.ª versão nova pergunta se quer atualizar
/// e depois se as próximas devem ser automáticas ou perguntadas; modo "auto" instala sozinho quando a app não está ocupada;
/// modo "manual" avisa uma vez e deixa o aviso no rodapé do menu.
/// </summary>
internal static class UpdateFlow
{
    private static DispatcherTimer? _timer;
    private static bool _prompting, _installing;
    private static Func<bool>? _isHidden;

    public const string ModeAuto = "auto", ModeManual = "manual";

    /// <summary>Chamar uma vez com a janela principal já visível. <paramref name="isHidden"/> diz se a janela está na bandeja.</summary>
    public static void Start(Func<bool> isHidden)
    {
        if (_timer != null) return;
        _isHidden = isHidden;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        _timer.Tick += async (_, _) =>
        {
            _timer.Interval = TimeSpan.FromHours(6);
            await CheckAsync(userAsked: false);
        };
        _timer.Start();
    }

    /// <summary>Procura agora. Com <paramref name="userAsked"/> mostra também "está em dia" e os erros.</summary>
    public static async Task CheckAsync(bool userAsked, DependencyObject? from = null)
    {
        UpdateInfo? info;
        try { info = await Updater.CheckAsync(); }
        catch (Exception ex)
        {
            if (userAsked) GamerDialogs.Info(from, "Não consegui procurar atualizações", ex.Message, warn: true);
            return;
        }
        if (info == null)
        {
            if (userAsked) GamerDialogs.Info(from, "Está tudo em dia", $"Tens a versão mais recente do HyperCore (v{Updater.Current.ToString(3)}).");
            return;
        }
        await OnFoundAsync(info, userAsked, from);
    }

    private static async Task OnFoundAsync(UpdateInfo info, bool userAsked, DependencyObject? from)
    {
        var s = AppServices.Settings;
        if (_prompting || _installing) return;
        if (s.UpdateMode == null) { await FirstTimeAsync(info, from); return; }
        if (s.UpdateMode == ModeAuto && !userAsked) { await AutoAsync(info); return; }

        // Manual (ou o utilizador carregou em "Procurar"): pergunta logo se foi pedido; senão avisa uma vez por versão
        if (userAsked) { await PromptAsync(info, from); return; }
        if (s.UpdateNotifiedVersion != info.Tag)
        {
            s.UpdateNotifiedVersion = info.Tag;
            try { s.Save(); } catch { }
            AppServices.Toast($"Há uma versão nova do HyperCore (v{info.Version.ToString(3)}). Carrega no aviso no fundo do menu para atualizar.");
        }
    }

    /// <summary>1.ª vez: atualizar agora? e depois: automático ou perguntar?</summary>
    private static async Task FirstTimeAsync(UpdateInfo info, DependencyObject? from)
    {
        _prompting = true;
        bool now;
        try
        {
            ShowWindow();
            now = GamerDialogs.Confirm(from, "Há uma versão nova do HyperCore",
                $"A versão {info.Version.ToString(3)} já está disponível (tens a {Updater.Current.ToString(3)}). Queres atualizar agora? Demora menos de um minuto e a app reabre sozinha.",
                "Atualizar agora", NotesBullets(info), glyph: "", cancelLabel: "Agora não");

            bool auto = GamerDialogs.Confirm(from, "E as próximas versões?",
                "Escolhe como queres receber as atualizações daqui para a frente. Podes mudar isto em Configurações.",
                "Atualizar sozinho", new[]
                {
                    "Atualizar sozinho: quando sair uma versão nova, o HyperCore instala-a e reabre (nunca a meio de um jogo, scan ou Modo Jogo).",
                    "Perguntar-me antes: aparece um aviso e és tu que decides quando atualizar.",
                }, glyph: "", cancelLabel: "Perguntar-me antes");
            AppServices.Settings.UpdateMode = auto ? ModeAuto : ModeManual;
            AppServices.Settings.UpdateNotifiedVersion = info.Tag;
            try { AppServices.Settings.Save(); } catch { }
        }
        finally { _prompting = false; }
        if (now) await InstallInteractiveAsync(info, from);
    }

    /// <summary>Pergunta (modo manual ou aviso do menu).</summary>
    public static async Task PromptAsync(UpdateInfo info, DependencyObject? from)
    {
        if (_prompting || _installing) return;
        _prompting = true;
        bool yes;
        try
        {
            ShowWindow();
            yes = GamerDialogs.Confirm(from, $"HyperCore {info.Version.ToString(3)} disponível",
                $"Tens a versão {Updater.Current.ToString(3)}. A app fecha, instala a versão nova e reabre sozinha. As tuas definições ficam como estão.",
                "Atualizar agora", NotesBullets(info), glyph: "", cancelLabel: "Mais tarde");
        }
        finally { _prompting = false; }
        if (yes) await InstallInteractiveAsync(info, from);
    }

    /// <summary>Modo automático: transfere em segundo plano e instala quando a app não está ocupada.</summary>
    private static async Task AutoAsync(UpdateInfo info)
    {
        if (Updater.BusyReason() != null) { RetryAutoLater(info); return; } // jogo/scan/Modo Jogo: tenta de novo daqui a 10 min
        _installing = true;
        try
        {
            string file;
            try { file = await Task.Run(() => Updater.DownloadAsync(info, null, CancellationToken.None)); }
            catch (Exception ex) { AppServices.Toast("A atualização automática falhou: " + ex.Message); return; }
            if (Updater.BusyReason() != null) { RetryAutoLater(info); return; } // o ficheiro já fica transferido

            AppServices.Toast($"A instalar o HyperCore {info.Version.ToString(3)}… a app reabre sozinha dentro de segundos.");
            await Task.Delay(TimeSpan.FromSeconds(6));
            if (Updater.BusyReason() != null) { RetryAutoLater(info); return; }
            bool hidden = _isHidden?.Invoke() ?? false;
            var (ok, msg) = await Task.Run(() => Updater.Install(file, hidden));
            if (ok) Application.Current.Shutdown();
            else AppServices.Toast(msg);
        }
        finally { _installing = false; }
    }

    private static DispatcherTimer? _retry;

    private static void RetryAutoLater(UpdateInfo info)
    {
        if (_retry != null) return;
        _retry = new DispatcherTimer { Interval = TimeSpan.FromMinutes(10) };
        _retry.Tick += async (_, _) =>
        {
            _retry!.Stop();
            _retry = null;
            if (AppServices.Settings.UpdateMode == ModeAuto && !_installing && !_prompting) await AutoAsync(info);
        };
        _retry.Start();
    }

    private static async Task InstallInteractiveAsync(UpdateInfo info, DependencyObject? from)
    {
        if (Updater.BusyReason() is string why)
        {
            GamerDialogs.Info(from, "Agora não dá para atualizar", $"Agora não dá porque {why}. Quando acabares, carrega outra vez no aviso no fundo do menu.", warn: true);
            return;
        }
        _installing = true;
        try
        {
            var (win, bar, text, cancelBtn) = ProgressWindow(from, info);
            using var cts = new CancellationTokenSource();
            cancelBtn.Click += (_, _) => cts.Cancel();
            win.Show();
            try
            {
                var progress = new Progress<double>(p =>
                {
                    bar.Value = p * 100;
                    text.Text = info.Size > 0
                        ? $"A transferir… {p * info.Size / 1048576.0:0} de {info.Size / 1048576.0:0} MB"
                        : $"A transferir… {p * 100:0}%";
                });
                string file = await Task.Run(() => Updater.DownloadAsync(info, progress, cts.Token));
                cancelBtn.IsEnabled = false;
                bar.IsIndeterminate = true;
                text.Text = "A instalar e a reabrir…";
                var (ok, msg) = await Task.Run(() => Updater.Install(file));
                if (ok) { Application.Current.Shutdown(); return; }
                win.Close();
                GamerDialogs.Info(from, "A atualização não foi instalada", msg, warn: true);
            }
            catch (OperationCanceledException) { win.Close(); }
            catch (Exception ex)
            {
                win.Close();
                GamerDialogs.Info(from, "A atualização não foi instalada", ex.Message, warn: true);
            }
        }
        finally { _installing = false; }
    }

    /// <summary>Até 6 linhas das notas da versão, sem marcação.</summary>
    private static IEnumerable<string> NotesBullets(UpdateInfo info)
    {
        var lines = info.Notes.Replace("\r", "").Split('\n')
            .Select(l => l.Trim().TrimStart('-', '*', '#', ' ').Replace("**", "").Trim())
            .Where(l => l.Length > 2 && !l.StartsWith("🤖") && !l.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            .Take(6).ToList();
        return lines.Count > 0 ? lines : new[] { "Correções e melhorias." };
    }

    private static void ShowWindow()
    {
        if (Application.Current?.MainWindow is { } w)
        {
            if (!w.IsVisible) w.Show();
            if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
            w.Activate();
        }
    }

    private static (Window win, ProgressBar bar, TextBlock text, Button cancel) ProgressWindow(DependencyObject? from, UpdateInfo info)
    {
        var owner = from != null ? Window.GetWindow(from) : Application.Current?.MainWindow;
        var w = new Window
        {
            WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent, ResizeMode = ResizeMode.NoResize,
            SizeToContent = SizeToContent.WidthAndHeight, ShowInTaskbar = false, Title = "HyperCore", Owner = owner is { IsVisible: true } ? owner : null,
            WindowStartupLocation = owner is { IsVisible: true } ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
        };
        Ui.UseTheme(w);
        var body = new StackPanel { Width = 420 };
        body.Children.Add(Ui.T($"A atualizar para a versão {info.Version.ToString(3)}", "H2", 18));
        var text = Ui.T("A ligar ao GitHub…", "MutedText");
        text.Margin = new Thickness(0, 6, 0, 12);
        body.Children.Add(text);
        var bar = new ProgressBar { Height = 6, Maximum = 100, Foreground = Ui.Res("Accent"), Background = Ui.Res("Line"), BorderThickness = new Thickness(0) };
        body.Children.Add(bar);
        var cancel = Ui.Btn("Cancelar", (_, _) => { });
        cancel.HorizontalAlignment = HorizontalAlignment.Right;
        cancel.Margin = new Thickness(0, 16, 0, 0);
        body.Children.Add(cancel);
        w.Content = new Border
        {
            Background = Ui.Res("Card"), BorderBrush = Ui.Res("LineStrong"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14),
            Padding = new Thickness(24, 20, 24, 20), Margin = new Thickness(16), Child = body,
            Effect = new DropShadowEffect { BlurRadius = 24, ShadowDepth = 4, Opacity = 0.55, Color = Colors.Black },
        };
        w.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) try { w.DragMove(); } catch { } };
        return (w, bar, text, cancel);
    }

    /// <summary>Liga o texto da versão no rodapé do menu: mostra "Nova versão" quando há uma, e abre a pergunta ao clicar.</summary>
    public static void Attach(TextBlock versionLabel)
    {
        string normal = $"HyperCore v{Updater.Current.ToString(3)}";
        var normalBrush = versionLabel.Foreground;
        void Refresh()
        {
            if (Updater.Available is { } info)
            {
                versionLabel.Text = $"↑ Nova versão v{info.Version.ToString(3)} · atualizar";
                versionLabel.Foreground = Ui.Res("Accent2");
                versionLabel.Cursor = Cursors.Hand;
                versionLabel.ToolTip = $"Tens a v{Updater.Current.ToString(3)}. Carrega para atualizar.";
            }
            else
            {
                versionLabel.Text = normal;
                versionLabel.Foreground = normalBrush;
                versionLabel.Cursor = null;
                versionLabel.ToolTip = null;
            }
        }
        Updater.AvailableChanged += () => versionLabel.Dispatcher.BeginInvoke(Refresh);
        versionLabel.MouseLeftButtonUp += async (_, _) =>
        {
            if (Updater.Available is { } info) await PromptAsync(info, versionLabel);
        };
        Refresh();
    }
}
