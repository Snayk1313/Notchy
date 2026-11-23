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
    public const string Version = "2.5";

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
        new("side", Color.FromRgb(0x7E, 0xE0, 0xC8), "Novo", "New", "Ilha nas laterais", "Island on the sides",
            "A ilha pode ficar em pé na borda esquerda ou direita da tela, com tudo o que ela faz no topo.",
            "The island can stand on the left or right edge of the screen, doing everything it does at the top."),
        new("notices", Color.FromRgb(0x9A, 0xD7, 0xFF), "Melhor", "Better", "Print e download em cards", "Capture and download cards",
            "O aviso do print e do download concluído vira um card embaixo da ilha, que abre a qualquer momento.",
            "The capture and finished-download notices become a card under the island, which opens any time."),
        new("chat", Color.FromRgb(0xFF, 0xAA, 0x4C), "Melhor", "Better", "Notchy Chat mais rápido", "Faster Notchy Chat",
            "Corrigir e Traduzir ficaram bem mais rápidos, e o botão Substituir aparece em mais apps.",
            "Fix spelling and Translate got much faster, and the Replace button shows up in more apps."),
    };

    public static readonly Section[] Sections =
    {
        new("Ilha nas laterais (nova)", "Island on the sides (new)",
            new[]
            {
                "A ilha pode ficar em pé na borda esquerda ou direita da tela, além do topo, com os mesmos estados: olhos, música e letra, Claude, foco, download, volume e brilho, bolhas e notificações.",
                "Aberta na lateral, as abas ficam numa coluna em pé, a troca de abas desliza para cima e para baixo, e cada página usa a altura toda: Início, Música com a letra embaixo da capa, Foco, Relógio com o timer e os alarmes juntos, Calendário com a grade maior e Clima com mais um dia.",
                "A barra de volume fica em pé e troca a saída de áudio numa lista ao lado dela.",
                "O lembrete do calendário e o sininho do alarme aparecem na pílula em pé, alinhados com os olhinhos."
            },
            new[]
            {
                "The island can stand on the left or right edge of the screen, besides the top, with the same states: eyes, music and lyrics, Claude, focus, download, volume and brightness, bubbles and notifications.",
                "Open on a side, the tabs sit in a standing column, switching tabs slides up and down, and every page uses the whole height: Home, Music with the lyrics under the cover, Focus, Clock with the timer and alarms together, Calendar with a bigger month and Weather with one more day.",
                "The volume bar stands up and switches the audio output in a list beside it.",
                "The calendar reminder and the alarm bell show on the standing pill, lined up with the eyes."
            }),
        new("Posição", "Position",
            new[]
            {
                "Nova aba Posição nas Configurações: o lugar normal (esquerda, topo ou direita), arrastar a ilha e regras.",
                "Regras: \u201cQuando [app] estiver em tela cheia ou em primeiro plano, a ilha vai para [lado]\u201d. Ela muda de lugar em menos de um segundo e volta quando a condição acaba.",
                "Arraste a ilha até o topo ou uma lateral: ela fica em pé aos poucos perto da borda e encaixa com um assentar leve.",
                "A troca de lugar desliza para fora e para dentro da borda, e as bolhas vão junto com a ilha."
            },
            new[]
            {
                "New Position tab in Settings: the usual place (left, top or right), dragging the island and rules.",
                "Rules: \u201cWhen [app] is full screen or in front, the island moves to [side]\u201d. It moves in under a second and goes back when that stops.",
                "Drag the island to the top or a side: it stands up bit by bit near the edge and snaps in with a soft settle.",
                "Changing place slides out of and into the edge, and the bubbles go along with the island."
            }),
        new("Avisos em cards", "Notices as cards",
            new[]
            {
                "O aviso do print virou um card embaixo da ilha, na mesma fila das notificações, com a miniatura e os botões Copiar, Salvar e Editar. A ilha abre a qualquer momento enquanto ele está na tela.",
                "O download concluído também vira card, com o arquivo e os botões Abrir e Mostrar na pasta."
            },
            new[]
            {
                "The capture notice became a card under the island, in the same line as notifications, with the thumbnail and Copy, Save and Edit. The island opens any time while it's showing.",
                "A finished download is a card too, with the file and Open and Show in folder."
            }),
        new("Música", "Music",
            new[]
            {
                "A capa no player cresce e inclina com o mouse, como os cards da página inicial, mostra no canto o ícone do app que está tocando, e o clique abre esse app."
            },
            new[]
            {
                "The player's cover grows and tilts with the mouse, like the home cards, shows the playing app's icon in its corner, and a click opens that app."
            }),
        new("Notchy Chat", "Notchy Chat",
            new[]
            {
                "Corrigir e Traduzir ficaram bem mais rápidos.",
                "O botão Substituir aparece em mais apps.",
                "Um clique fora do Notchy fecha a ilha que tinha ficado presa aberta depois de uma correção."
            },
            new[]
            {
                "Fix spelling and Translate got much faster.",
                "The Replace button shows up in more apps.",
                "A click outside Notchy closes the island if it got stuck open after a fix."
            }),
        new("Correções", "Fixes",
            new[]
            {
                "Downloads rápidos entram no histórico de Downloads.",
                "Os cliques não ficam mais bloqueados enquanto o print sobe até a ilha.",
                "Ao clicar numa notificação, a ilha fecha uma vez, sem reabrir, e o app abre mais rápido no primeiro clique.",
                "A página do Clima não fecha ao trocar a localização entre automática e manual.",
                "O brilho de \u201ccopiado\u201d aparece junto da ilha, e os botões Cancelar e Salvar do alarme não ficam mais cortados."
            },
            new[]
            {
                "Quick downloads show up in the Downloads history.",
                "Clicks are no longer blocked while the capture flies up to the island.",
                "Clicking a notification closes the island once, without opening it again, and the app opens faster on the first click.",
                "The Weather page no longer closes when switching the location between automatic and manual.",
                "The \u201ccopied\u201d glow shows right by the island, and the alarm's Cancel and Save buttons are no longer cut."
            }),
    };
}
