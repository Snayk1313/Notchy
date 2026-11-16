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
    public const string Version = "2.3";

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
        new("clip", Color.FromRgb(0x8F, 0xB8, 0xFF), "Novo", "New", "Histórico de cópias", "Clipboard history",
            "Tudo o que você copia fica numa aba da ilha. Win + Alt + V abre de qualquer lugar e Enter cola onde você estava.",
            "Everything you copy stays in a tab on the island. Win + Alt + V opens it from anywhere and Enter pastes where you were."),
        new("calendar", Color.FromRgb(0x7C, 0xC8, 0xFF), "Novo", "New", "Agendas conectadas", "Connected calendars",
            "Os eventos do Google Agenda, Outlook ou iCloud aparecem no calendário da ilha, com a cor de cada agenda.",
            "Events from Google Calendar, Outlook or iCloud show on the island's calendar, in each calendar's colour."),
        new("notices", Color.FromRgb(0xFF, 0x7E, 0xB6), "Melhor", "Better", "Notificações por conversa", "Notifications by conversation",
            "Mensagens da mesma pessoa ficam numa pilha. Limpe um app, uma conversa ou uma por uma.",
            "Messages from the same person stack up. Clear an app, a conversation or one at a time."),
        new("level", Color.FromRgb(0xFF, 0xFF, 0xFF), "Melhor", "Better", "Volume em tela cheia", "Volume in full screen",
            "Em jogos e vídeos, a barra de volume e brilho desce sozinha do topo, sem mostrar a ilha.",
            "In games and videos, the volume and brightness bar drops down on its own, without showing the island."),
    };

    public static readonly Section[] Sections =
    {
        new("Histórico de cópias (novo)", "Clipboard history (new)",
            new[]
            {
                "Uma aba nova com textos, links, cores, imagens e arquivos que você copiou. Busque, filtre por tipo, fixe no topo e copie de novo com um clique.",
                "Win + Alt + V abre o histórico de qualquer lugar: escolha com as setas e aperte Enter para colar no app que você estava usando.",
                "O texto das imagens copiadas é lido, então dá para achar um print pela busca.",
                "Nada é guardado de gerenciadores de senha, janelas anônimas e do que o Windows marca para não guardar. Dá para pausar e escolher apps ignorados.",
                "Com a ilha fechada, o contorno brilha um instante quando você copia algo."
            },
            new[]
            {
                "A new tab with the texts, links, colours, images and files you copied. Search, filter by kind, pin to the top and copy again with a click.",
                "Win + Alt + V opens the history from anywhere: choose with the arrows and press Enter to paste into the app you were using.",
                "The text in copied images is read, so you can find a screenshot by searching.",
                "Nothing is kept from password managers, private windows or what Windows marks as not to keep. You can pause it and choose apps to ignore.",
                "With the island closed, its outline glows for a moment when you copy something."
            }),
        new("Calendário", "Calendar",
            new[]
            {
                "Conecte o Google Agenda, Outlook ou iCloud em Configurações → Calendário e clima, com um passo a passo para cada um.",
                "Os eventos de fora aparecem com a cor e o nome da agenda, e os avisos podem ser ligados ou desligados por agenda.",
                "Mude o dia de um evento pelo mini calendário do editor ou arrastando o evento para outro dia."
            },
            new[]
            {
                "Connect Google Calendar, Outlook or iCloud in Settings → Calendar and weather, with step-by-step help for each.",
                "Their events show in the calendar's colour and with its name, and reminders can be turned on or off per calendar.",
                "Change an event's day with the editor's mini calendar or by dragging the event to another day."
            }),
        new("Notificações", "Notifications",
            new[]
            {
                "Mensagens da mesma conversa ficam numa pilha com o número delas, e os apps aparecem com o próprio ícone.",
                "Limpe um app inteiro, uma conversa ou uma notificação arrastando para o lado, sempre com Desfazer.",
                "A lista ganhou barra de rolagem quando fica comprida."
            },
            new[]
            {
                "Messages from the same conversation stack up with their count, and apps show their own icon.",
                "Clear a whole app, a conversation or one notification by swiping it away, always with Undo.",
                "The list got a scroll bar when it gets long."
            }),
        new("Volume e tela cheia", "Volume and full screen",
            new[]
            {
                "A barra de volume e brilho desce sozinha do topo e sobe de volta, sem mostrar a ilha.",
                "Com a ilha aberta, segure Shift e gire a roda do mouse para mudar o volume.",
                "Nova opção em Geral para mostrar alarmes e timers tocando mesmo em tela cheia."
            },
            new[]
            {
                "The volume and brightness bar drops down from the top and goes back up, without showing the island.",
                "With the island open, hold Shift and turn the mouse wheel to change the volume.",
                "A new option in General to show ringing alarms and timers even in full screen."
            }),
        new("Animações", "Animations",
            new[]
            {
                "Ao fechar a ilha com uma página aberta, ela esmaece como as abas. Volte em até 3 segundos e ela está no mesmo lugar.",
                "Uma página aberta de dentro de outra desliza, itens novos entram suavemente nas listas e as opções das Configurações abrem e recolhem sem pular.",
                "A troca de música, o play e pause, os olhos virando sorriso, a troca de abas, os menus e o sininho agora se movem em vez de trocar de uma vez."
            },
            new[]
            {
                "Closing the island with a page open, it fades like the tabs. Come back within 3 seconds and it's still there.",
                "A page opened from another slides in, new items glide into lists and options in Settings open and close without jumping.",
                "Changing songs, play and pause, the eyes turning into a smile, switching tabs, the menus and the bell now move instead of switching at once."
            }),
        new("Configurações e primeira abertura", "Settings and first run",
            new[]
            {
                "Páginas novas Copiados e Relógio, sons do Notchy numa seção só, Spotify mais limpo depois de conectar e a barra lateral em grupos.",
                "O assistente da primeira abertura ganhou os olhinhos, um quadro animado em cada passo, os passos de Copiados e agendas e um tour mostrado na própria ilha."
            },
            new[]
            {
                "New Clipboard and Clock pages, Notchy's sounds in one section, a cleaner Spotify once connected and the sidebar in groups.",
                "The first-run assistant got the little eyes, an animated picture on each step, the Clipboard and calendars steps and a tour shown on the island itself."
            }),
        new("Correções", "Fixes",
            new[]
            {
                "O contorno do Claude não fica mais na tela quando a barra de volume some em tela cheia.",
                "A ilha não aparece mais por alguns segundos ao entrar em tela cheia, nem ao parar um alarme em tela cheia.",
                "Os olhos não ficam mais por cima do nome da música.",
                "Os botões das Configurações não ficam mais por baixo do conteúdo ao rolar."
            },
            new[]
            {
                "Claude's outline no longer stays on screen when the volume bar goes away in full screen.",
                "The island no longer shows for a few seconds when entering full screen, nor when stopping an alarm in full screen.",
                "The eyes no longer sit over the song's name.",
                "Settings' buttons no longer end up under the content when scrolling."
            }),
    };
}
