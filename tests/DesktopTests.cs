using SteamVrTranslator;

public static class DesktopTests
{
    public static void Run()
    {
        using var image = new Bitmap(200, 100);
        using (var g = Graphics.FromImage(image)) { g.Clear(Color.Navy); g.FillRectangle(Brushes.Red, 50, 25, 100, 50); }
        using var cropped = CaptureService.Crop(image, .25f, .25f, .5f, .5f);
        if (cropped.Size != new Size(100, 50) || cropped.GetPixel(0, 0).R != 255) throw new Exception("Crop coordinates or dimensions are wrong");
        try { using var invalid = CaptureService.Crop(image, .9f, 0, .2f, 1); throw new Exception("Out of bounds crop accepted"); } catch (ArgumentException) { }
        try { using var invalid = CaptureService.Crop(image, float.NaN, 0, .2f, 1); throw new Exception("NaN crop accepted"); } catch (ArgumentException) { }
        if (Hotkeys.Parse("Ctrl+Alt+F8") != (0x4003U, (uint)Keys.F8)) throw new Exception("Hotkey parse failed");
        try { Hotkeys.Parse("Ctrl+Alt"); throw new Exception("Missing main key accepted"); } catch (ArgumentException) { }
    }
}
