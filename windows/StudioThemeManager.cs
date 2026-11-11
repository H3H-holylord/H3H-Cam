using System.Windows;
using System.Windows.Media;

namespace S8Cam;

/// <summary>
/// Авторская система визуальных тем «Два характера в одном корпусе»:
/// 1. Cine Rig — тёмный режиссёрский киномонитор (ARRI / SmallHD: тёплый графит, вольфрамовый янтарь, фосфорный лайм, эфирный Tally).
/// 2. Nordic Atelier — светлый тактильный студийный прибор (Braun / Teenage Engineering: тёплый анодированный камень, бумажно-белые карточки, сигнальный оранжевый).
/// Переключение выполняется за &lt; 1 мс через DynamicResource без остановки видеопотока.
/// </summary>
public static class StudioThemeManager {
    public const string CineRig = "cine_rig";
    public const string NordicAtelier = "nordic_atelier";

    public static string Normalize(string? theme) =>
        string.Equals(theme, NordicAtelier, StringComparison.OrdinalIgnoreCase) ? NordicAtelier : CineRig;

    public static bool IsNordic(string? theme) => Normalize(theme) == NordicAtelier;

    private static SolidColorBrush Frozen(byte r, byte g, byte b, byte a = 255) {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    public static void Apply(string? themeName, ResourceDictionary? target = null) {
        var dict = target ?? Application.Current?.Resources;
        if (dict == null) return;

        bool nordic = IsNordic(themeName);

        if (!nordic) {
            // ХАРАКТЕР 1: CINE RIG (Тёмный режиссёрский киномонитор)
            dict["BgWindowBrush"] = Frozen(0x14, 0x13, 0x12);
            dict["PanelBrush"] = Frozen(0x1C, 0x1B, 0x19);
            dict["BgCardBrush"] = Frozen(0x24, 0x22, 0x1F);
            dict["BgInputBrush"] = Frozen(0x12, 0x11, 0x10);
            dict["BgElevatedBrush"] = Frozen(0x2A, 0x28, 0x24);
            dict["BorderSubtleBrush"] = Frozen(0x35, 0x32, 0x2E);
            dict["BorderStrongBrush"] = Frozen(0x4A, 0x45, 0x3E);

            dict["TextPrimaryBrush"] = Frozen(0xF4, 0xF1, 0xEA);
            dict["TextSecondaryBrush"] = Frozen(0xC8, 0xC2, 0xB8);
            dict["TextMutedBrush"] = Frozen(0x91, 0x8B, 0x80);

            // Вольфрамовый янтарь (главный акцент оптики и управления)
            dict["AccentPrimaryBrush"] = Frozen(0xF5, 0x9E, 0x0B);
            dict["AccentPrimaryTextBrush"] = Frozen(0x16, 0x11, 0x06);
            dict["AccentSoftBgBrush"] = Frozen(0x30, 0x24, 0x11);

            // Фосфорный лайм (сигнал, здоровье потока, нормальный звук)
            dict["AccentLiveBrush"] = Frozen(0x84, 0xCC, 0x16);
            dict["AccentLiveTextBrush"] = Frozen(0x11, 0x1A, 0x05);
            dict["AccentLiveSoftBgBrush"] = Frozen(0x1E, 0x2B, 0x12);

            // Эфирный алый Tally (ON AIR, REC, MUTE)
            dict["AccentTallyBrush"] = Frozen(0xEF, 0x44, 0x44);
            dict["AccentTallySoftBgBrush"] = Frozen(0x36, 0x16, 0x18);

            // Кнопки прибора
            dict["ButtonSurfaceBrush"] = Frozen(0x28, 0x26, 0x23);
            dict["ButtonHoverBrush"] = Frozen(0x34, 0x31, 0x2C);
            dict["ButtonBorderBrush"] = Frozen(0x3E, 0x3A, 0x34);

            // Колодец видоискателя и логов
            dict["MonitorWellBrush"] = Frozen(0x0B, 0x0B, 0x0A);
            dict["ConsoleWellBrush"] = Frozen(0x11, 0x10, 0x0F);
            dict["ModalBackdropBrush"] = Frozen(0x10, 0x0F, 0x0E, 0xDC);
        } else {
            // ХАРАКТЕР 2: NORDIC ATELIER (Светлый тактильный студийный прибор)
            dict["BgWindowBrush"] = Frozen(0xEC, 0xEA, 0xE4);
            dict["PanelBrush"] = Frozen(0xE1, 0xDE, 0xD6);
            dict["BgCardBrush"] = Frozen(0xF8, 0xF7, 0xF3);
            dict["BgInputBrush"] = Frozen(0xFF, 0xFF, 0xFD);
            dict["BgElevatedBrush"] = Frozen(0xEF, 0xED, 0xE7);
            dict["BorderSubtleBrush"] = Frozen(0xCE, 0xC9, 0xBE);
            dict["BorderStrongBrush"] = Frozen(0xB2, 0xAC, 0x9F);

            dict["TextPrimaryBrush"] = Frozen(0x18, 0x18, 0x16);
            dict["TextSecondaryBrush"] = Frozen(0x42, 0x40, 0x3B);
            dict["TextMutedBrush"] = Frozen(0x6C, 0x68, 0x60);

            // Сигнальный оранжевый (механический акцент клавиш и режимов)
            dict["AccentPrimaryBrush"] = Frozen(0xFF, 0x55, 0x00);
            dict["AccentPrimaryTextBrush"] = Frozen(0xFF, 0xFF, 0xFF);
            dict["AccentSoftBgBrush"] = Frozen(0xFC, 0xE6, 0xDA);

            // Приборный изумруд (активный поток и готовность)
            dict["AccentLiveBrush"] = Frozen(0x15, 0x80, 0x3D);
            dict["AccentLiveTextBrush"] = Frozen(0xFF, 0xFF, 0xFF);
            dict["AccentLiveSoftBgBrush"] = Frozen(0xDC, 0xFC, 0xE7);

            // Карминовый Tally
            dict["AccentTallyBrush"] = Frozen(0xDC, 0x26, 0x26);
            dict["AccentTallySoftBgBrush"] = Frozen(0xFE, 0xE2, 0xE2);

            // Механические клавиши
            dict["ButtonSurfaceBrush"] = Frozen(0xFB, 0xFA, 0xF7);
            dict["ButtonHoverBrush"] = Frozen(0xF0, 0xED, 0xE6);
            dict["ButtonBorderBrush"] = Frozen(0xC5, 0xBF, 0xB2);

            // Видоискатель остаётся тёмным оптическим колодцем для точной оценки экспозиции!
            dict["MonitorWellBrush"] = Frozen(0x12, 0x12, 0x11);
            dict["ConsoleWellBrush"] = Frozen(0xF4, 0xF2, 0xEC);
            dict["ModalBackdropBrush"] = Frozen(0x1E, 0x1D, 0x1B, 0xB8);
        }
    }
}
