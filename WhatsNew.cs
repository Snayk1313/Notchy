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
    public const string Version = "2.2";

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
        new("calendar", Color.FromRgb(0x7C, 0xC8, 0xFF), "Novo", "New", "Calendário", "Calendar",
            "Clique na data da ilha para ver o mês, criar eventos com aviso e acompanhar feriados e aniversários.",
            "Click the date on the island to see the month, create events with reminders and follow holidays and birthdays."),
        new("chat", Color.FromRgb(0xFF, 0xAA, 0x4C), "Novo", "New", "Chat grátis", "Free Chat",
            "O Notchy Chat agora também funciona com o Gemini, usando uma chave grátis do Google.",
            "Notchy Chat now also works with Gemini, using a free key from Google."),
        new("notices", Color.FromRgb(0xFF, 0x7E, 0xB6), "Novo", "New", "Notificações só no Notchy", "Notifications only on Notchy",
            "Avisos e sons do Windows ficam em silêncio e tudo aparece na ilha, até em tela cheia, com um som próprio.",
            "Windows' pop-ups and sounds stay quiet and everything shows on the island, even in full screen, with its own sound."),
        new("settings", Color.FromRgb(0xA7, 0x8B, 0xFA), "Melhor", "Better", "Configurações organizadas", "Tidier settings",
            "Novas abas para Notificações, Calendário e clima, Som e dispositivos e Notchy Chat.",
            "New tabs for Notifications, Calendar and weather, Sound and devices and Notchy Chat."),
    };

    public static readonly Section[] Sections =
    {
        new("Calendário (novo)", "Calendar (new)",
            new[]
            {
                "Clique na data da ilha para abrir o calendário, com os eventos do dia e os próximos.",
                "Eventos com repetição, aviso antes, lembrar de novo e cores por tipo.",
                "Aniversários com idade, feriados do Brasil e festinha com confete.",
                "Clique no mês ou no ano para pular rapidamente de data."
            },
            new[]
            {
                "Click the date on the island to open the calendar, with today's and upcoming events.",
                "Events with repeats, early reminders, snooze and colors by type.",
                "Birthdays with age, Brazilian holidays and a little confetti party.",
                "Click the month or the year to jump quickly to a date."
            }),
        new("Notchy Chat", "Notchy Chat",
            new[]
            {
                "Opção de usar o Gemini com uma chave grátis do Google, para quem não tem o Claude Code.",
                "Aba própria nas Configurações, com quem responde e o idioma da tradução."
            },
            new[]
            {
                "An option to use Gemini with a free key from Google, for those without Claude Code.",
                "A tab of its own in Settings, with who answers and the translation language."
            }),
        new("Notificações", "Notifications",
            new[]
            {
                "Nova opção Só no Notchy: o aviso e o som do Windows somem, e as notificações continuam na central.",
                "O cartão pode aparecer em tela cheia, com o mesmo fundo da ilha.",
                "Som próprio para notificações, com opção de ligar e desligar.",
                "Programas como o Lightshot agora mostram o ícone."
            },
            new[]
            {
                "A new Only on Notchy option: Windows' pop-up and sound go away, and notifications stay in the notification center.",
                "The card can show in full screen, with the same background as the island.",
                "A sound of its own for notifications, which you can turn on and off.",
                "Programs like Lightshot now show their icon."
            }),
        new("Instalação e atualização", "Installing and updating",
            new[]
            {
                "Quem instala do zero agora tem um assistente com idioma, aparência, notificações e mais. Dá para abrir de novo em Configurações → Geral.",
                "O download das atualizações mostra versão, tamanho e progresso em Configurações → Sobre.",
                "A partir da próxima atualização, a instalação aparece num cartão no estilo do Notchy."
            },
            new[]
            {
                "New installs get an assistant with language, look, notifications and more. You can open it again in Settings → General.",
                "Update downloads show the version, size and progress in Settings → About.",
                "From the next update on, installing shows in a card in Notchy's own style."
            }),
        new("Melhorias", "Improvements",
            new[]
            {
                "Pílula na página de clima para atualizar na hora, mostrando há quanto tempo foi atualizado.",
                "Configurações reorganizadas em abas por assunto.",
                "Clima, Spotify, letras e atualizações conectam mais rápido em redes com IPv6 lento."
            },
            new[]
            {
                "A pill on the weather page to refresh right away, showing how long ago it was updated.",
                "Settings reorganized into tabs by subject.",
                "Weather, Spotify, lyrics and updates connect faster on networks with slow IPv6."
            }),
        new("Correções", "Fixes",
            new[]
            {
                "Pedidos de permissão do Claude não ficam mais escondidos atrás de outro chat.",
                "Downloads pequenos que terminam antes de você escolher a pasta agora contam como concluídos.",
                "Erros de atualização tentam de novo sozinhos e explicam o que houve."
            },
            new[]
            {
                "Claude's permission requests no longer hide behind another chat.",
                "Small downloads that finish before you pick the folder now count as completed.",
                "Update errors retry on their own and explain what happened."
            }),
    };
}
