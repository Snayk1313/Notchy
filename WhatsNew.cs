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
    public const string Version = "2.6";

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
        new("buds", Color.FromRgb(0x6C, 0xE6, 0xA0), "Novo", "New", "Dispositivos", "Devices",
            "A bateria do headset, do mouse e dos fones ao lado do sino, com o aviso de bateria fraca na ilha.",
            "Your headset, mouse and earbuds battery next to the bell, with a low battery warning on the island."),
        new("capture", Color.FromRgb(0x9A, 0xD7, 0xFF), "Melhor", "Better", "Captura com holofote", "Capture spotlight",
            "Na captura, a parte da janela sob o mouse fica acesa e o resto escurece; a rodinha sobe até a janela inteira.",
            "In the capture, the part of the window under the mouse stays lit and the rest darkens; the wheel goes up to the whole window."),
        new("notices", Color.FromRgb(0xB9, 0xA6, 0xFF), "Melhor", "Better", "Notificações em bolha", "Notifications as bubbles",
            "Toda notificação desce numa bolha embaixo da ilha, e só o contorno da ilha acende na cor do app.",
            "Every notification comes down as a bubble under the island, and only the island's outline lights up in the app's colour."),
    };

    public static readonly Section[] Sections =
    {
        new("Dispositivos (novo)", "Devices (new)",
            new[]
            {
                "Uma bateria pequena ao lado do sino mostra o aparelho sem fio mais fraco, e a porcentagem aparece quando ela está fraca. O clique abre “‹ Dispositivos”, com cada aparelho, a porcentagem e um raio quando está carregando.",
                "Headset HyperX Cloud Alpha Wireless e mouses Rapoo lidos direto do receptor USB, sem precisar do app da marca. Fones e caixas Bluetooth com a bateria que o Windows informa.",
                "Headset com bateria fraca: aviso na ilha em 15% e em 5%, como os fones. Um clique na ilha de bateria fraca abre Dispositivos.",
                "Em notebook, a bateria do PC virou “Este PC” em Dispositivos, e o cabeçalho ficou com uma bateria só.",
                "Com pouco espaço no cabeçalho, sai primeiro a waveform, depois o texto do Atualizar e por último a porcentagem: nada se sobrepõe ao relógio."
            },
            new[]
            {
                "A small battery next to the bell shows your weakest wireless device, with its percentage when it's low. A click opens “‹ Devices”, with each device, its percentage and a bolt while charging.",
                "HyperX Cloud Alpha Wireless headsets and Rapoo mice read straight from their USB receiver, no brand app needed. Bluetooth earbuds and speakers with the battery Windows reports.",
                "Low headset battery: a warning on the island at 15% and 5%, like the earbuds. A click on the low battery island opens Devices.",
                "On laptops, the PC's battery became “This PC” in Devices, so the header has a single battery.",
                "When the header is short of room, the waveform goes first, then the Update label, then the percentage: nothing overlaps the clock."
            }),
        new("Captura de tela", "Screen capture",
            new[]
            {
                "Holofote: a parte da janela sob o mouse (imagem, botão, lista, campo de texto) fica acesa e o resto da tela escurece; o recorte desliza de uma para outra.",
                "A rodinha sobe para os grupos em volta e até a janela inteira, e desce de volta. O clique captura o que está aceso.",
                "Em apps que não informam suas partes (navegadores baseados no Firefox, apps como Discord e Spotify, jogos), acende a janela inteira, sem seleções estranhas.",
                "O Esc cancela a captura logo depois do Print Screen, antes de selecionar qualquer coisa."
            },
            new[]
            {
                "Spotlight: the part of the window under the mouse (an image, button, list, text box) stays lit and the rest of the screen darkens; the cut-out glides from one to the next.",
                "The wheel goes up to the groups around it and to the whole window, and back down. A click takes what's lit.",
                "In apps that don't report their parts (Firefox-based browsers, apps like Discord and Spotify, games), the whole window lights up, with no odd selections.",
                "Esc cancels the capture right after Print Screen, before anything is picked."
            }),
        new("Notificações", "Notifications",
            new[]
            {
                "Com a ilha parada no topo ou nas laterais, a notificação também desce numa bolha embaixo dela, como quando há algo tocando.",
                "Só a ilha brilha: o contorno dela acende uma vez na cor do app e apaga devagar; a bolha não tem mais brilho, em nenhuma posição."
            },
            new[]
            {
                "With the island idle at the top or on a side, a notification also comes down as a bubble under it, as when something is playing.",
                "Only the island glows: its outline lights up once in the app's colour and fades; the bubble has no glow anymore, in any position."
            }),
        new("Melhorias", "Improvements",
            new[]
            {
                "Prateleira: depois de soltar um arquivo na ilha, a próxima vez que ela abrir vai direto para a prateleira.",
                "Notchy Chat sabe a data de hoje e avisa quando uma informação pode estar desatualizada. Com uma chave do Gemini que permita, ele pesquisa no Google antes de responder."
            },
            new[]
            {
                "Shelf: after you drop a file on the island, the next time it opens it goes straight to the shelf.",
                "Notchy Chat knows today's date and says when something may be out of date. With a Gemini key that allows it, it searches Google before answering."
            }),
        new("Correções", "Fixes",
            new[]
            {
                "Os olhos não dormem mais com uma música pausada enquanto você usa o PC: só depois de 90 segundos sem mexer no mouse e no teclado."
            },
            new[]
            {
                "The eyes no longer doze off with a paused song while you're using the PC: only after 90 seconds without touching the mouse or keyboard."
            }),
    };
}
