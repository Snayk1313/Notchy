using System.Windows.Media;

namespace Notchy;

/// <summary>
/// What's new in this version, shown once on the island after an update (MainWindow.WhatsNew.cs) and from
/// Settings → About. Only versions with notes here show anything: small fixes (2.0.x) pass quietly.
/// Keep it in step with the release notes (the commit message that publishes the version).
/// </summary>
public static class WhatsNew
{
    /// <summary>The version these notes are for (major.minor).</summary>
    public const string Version = "2.1";

    public sealed record Highlight(string Art, Color Color, string TagPt, string TagEn, string TitlePt, string TitleEn, string TextPt, string TextEn)
    {
        public string Tag => Loc.Language == "en" ? TagEn : TagPt;
        public string Title => Loc.Language == "en" ? TitleEn : TitlePt;
        public string Text => Loc.Language == "en" ? TextEn : TextPt;
    }

    public sealed record Section(string TitlePt, string TitleEn, string[] ItemsPt, string[] ItemsEn)
    {
        public string Title => Loc.Language == "en" ? TitleEn : TitlePt;
        public string[] Items => Loc.Language == "en" ? ItemsEn : ItemsPt;
    }

    public static readonly Highlight[] Highlights =
    {
        new("clock", Color.FromRgb(0x7F, 0xD0, 0xFF), "Novo", "New", "Timer e alarmes", "Timers and alarms",
            "Nova aba Relógio, com vários timers, alarmes que se repetem e um sininho na ilha.",
            "A new Clock tab, with several timers, repeating alarms and a little bell on the island."),
        new("level", Color.FromRgb(0xFF, 0xD3, 0x6B), "Novo", "New", "Volume e brilho", "Volume and brightness",
            "O volume agora aparece na própria ilha, no lugar do aviso do Windows, com brilho e troca de saída de áudio.",
            "Volume now shows on the island itself, instead of Windows' pop-up, with brightness and switching audio outputs."),
        new("buds", Color.FromRgb(0x5E, 0xD6, 0x7D), "Novo", "New", "Fones Bluetooth", "Bluetooth earbuds",
            "Ao conectar os fones, a ilha mostra o nome e a bateria, e avisa quando ela estiver acabando.",
            "When you connect your earbuds, the island shows their name and battery, and warns you when it's running out."),
        new("weather", Color.FromRgb(0x7C, 0xC8, 0xFF), "Novo", "New", "Clima", "Weather",
            "Um novo cartão na tela inicial, com a temperatura de agora e a previsão de 3 dias.",
            "A new card on the home screen, with the current temperature and a 3-day forecast."),
        new("notices", Color.FromRgb(0xFF, 0x7E, 0xB6), "Novo", "New", "Notificações", "Notifications",
            "Mensagens do WhatsApp, Discord e Telegram descem da ilha com a foto de quem mandou. Clique para abrir a conversa.",
            "WhatsApp, Discord and Telegram messages drop from the island with the sender's photo. Click to open the chat."),
    };

    public static readonly Section[] Sections =
    {
        new("Relógio (novo)", "Clock (new)",
            new[]
            {
                "Nova aba Relógio, com páginas de Timer e Alarmes.",
                "Vários timers ao mesmo tempo, com a contagem na ilha.",
                "Alarmes com repetição por dia da semana e ajuste de 1 em 1 minuto.",
                "Quando toca, a ilha cresce e chama você, com opção de adiar.",
                "Com um alarme ligado, um sininho fica na ilha. Quando faltam 10 minutos, o horário aparece por 15 segundos.",
                "Volume próprio para o alarme, em Configurações → Sons."
            },
            new[]
            {
                "A new Clock tab, with Timer and Alarms pages.",
                "Several timers at once, counting down on the island.",
                "Alarms that repeat by day of the week, set minute by minute.",
                "When one rings, the island grows and calls you, with a snooze option.",
                "With an alarm on, a little bell stays on the island. With 10 minutes left, its time shows for 15 seconds.",
                "A volume of its own for the alarm, in Settings → Sounds."
            }),
        new("Volume e brilho (novo)", "Volume and brightness (new)",
            new[]
            {
                "Novo indicador de volume na própria ilha, no lugar do aviso do Windows (dá para desligar nas Configurações).",
                "Brilho na ilha, em notebooks e monitores compatíveis.",
                "Troque o dispositivo de áudio direto pela ilha."
            },
            new[]
            {
                "A new volume indicator on the island itself, instead of Windows' pop-up (you can turn it off in Settings).",
                "Brightness on the island, on laptops and compatible monitors.",
                "Switch audio devices right from the island."
            }),
        new("Fones Bluetooth (novo)", "Bluetooth earbuds (new)",
            new[]
            {
                "Ao conectar os fones, a ilha mostra o nome e a bateria.",
                "Aviso com brilho vermelho pulsando quando a bateria está baixa."
            },
            new[]
            {
                "When you connect your earbuds, the island shows their name and battery.",
                "A pulsing red glow warns you when the battery is low."
            }),
        new("Clima (novo)", "Weather (new)",
            new[]
            {
                "Novo cartão na tela inicial, com a temperatura de agora.",
                "Previsão de 3 dias.",
                "Use a localização do Windows ou escolha a cidade manualmente."
            },
            new[]
            {
                "A new card on the home screen, with the current temperature.",
                "A 3-day forecast.",
                "Use Windows' location or pick the city yourself."
            }),
        new("Notificações (novo)", "Notifications (new)",
            new[]
            {
                "As mensagens do WhatsApp, Discord, Telegram e outros apps descem da ilha, com a foto de quem mandou.",
                "Clique para abrir direto na conversa (no Discord, a conversa abre no primeiro clique; nos seguintes, abre só o app).",
                "Histórico das notificações na ilha, pelo sininho.",
                "Escolha quais apps aparecem nas Configurações."
            },
            new[]
            {
                "Messages from WhatsApp, Discord, Telegram and other apps drop from the island, with the sender's photo.",
                "Click to go straight to the chat (on Discord, the chat opens on the first click; after that, just the app).",
                "A history of your notifications on the island, behind the bell.",
                "Choose which apps show up in Settings."
            }),
        new("Prateleira e Downloads", "Shelf and Downloads",
            new[]
            {
                "Arraste vários arquivos de uma vez: Ctrl + clique, Shift + clique, botão Selecionar ou laço.",
                "Agora dá para arrastar os arquivos do histórico de Downloads para qualquer lugar.",
                "Ícones coloridos por tipo de arquivo nos Downloads."
            },
            new[]
            {
                "Drag several files at once: Ctrl + click, Shift + click, the Select button or a lasso.",
                "You can now drag files from the Downloads history anywhere.",
                "Colored icons by file type in Downloads."
            }),
        new("Menus", "Menus",
            new[]
            {
                "Novo menu da ilha em subpáginas: saída de áudio, fila do Spotify e clima.",
                "Novo menu na bandeja do sistema."
            },
            new[]
            {
                "A new island menu with sub-pages: audio output, Spotify queue and weather.",
                "A new system tray menu."
            }),
        new("Melhorias", "Improvements",
            new[]
            {
                "Com música tocando, a ilha abre direto no player. Para abrir um download ou o Claude, clique na bolha dele.",
                "O player mostra a duração total da música.",
                "Aba Foco com nome em português e novo ícone."
            },
            new[]
            {
                "With music playing, the island opens on the player. To open a download or Claude, click its bubble.",
                "The player shows the song's total length.",
                "The Focus tab has a new icon."
            }),
    };
}
