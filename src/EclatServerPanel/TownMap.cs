using System.IO;
using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace EclatServerPanel;

/// <summary>
/// Reads the Kanto Town Map from the administrator's own Fire Red ROM and
/// keeps the picture in the local profile. The repository holds only the
/// offsets and the hashes needed to check what was read, never game data.
/// </summary>
internal static class TownMap
{
    // Picture kept: the map area of the 240 × 160 screen, without the top bar.
    internal const int Left = 24, Top = 16, Width = 192, Height = 144;
    // Server grid cell (0, 0) starts at screen pixel (32, 32), like the game's cursor.
    internal const int GridX = (32 - Left) / 8, GridY = (32 - Top) / 8, Cells = Width / 8, Rows = Height / 8;
    internal static string CachePath => Path.Combine(ConnectionSettings.DirectoryPath, "town-map.png");

    private sealed record Source(string Label, int Tiles, int Tilemap, int Palette);
    // Original cartridges accepted by the game's launcher.
    private static readonly Dictionary<string, Source> Roms = new(StringComparer.OrdinalIgnoreCase)
    {
        ["fc663907256f06a3a09e2d6b967bc9af4919f111"] = new("Rouge Feu (France)", 0x3E7BD8, 0x3E8E58, 0x3E7898),
        ["41cb23d8dccc8ebd7c649cd8fbb58eeace6e2fdc"] = new("Fire Red (anglaise)", 0x3EF61C, 0x3F089C, 0x3EF2DC),
    };
    private const string TilesSha256 = "f9e8ddc403b2efcd9eaf87a8a1f16d9248f92d2372e42fb7aa88b09aed5fb3b4";
    private const string TilemapSha256 = "c9f38c5d52099958c18efe737a45ba04ce8101b0eb349e9d2243bf8324d82b49";
    private const string PaletteSha256 = "116382eeea3b668f188e80eb49f7440b1daeb0732cd81da2401da887e1e0e227";

    internal static BitmapSource? LoadCached()
    {
        try
        {
            if (!File.Exists(CachePath)) return null;
            using var stream = File.OpenRead(CachePath);
            var frame = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            if (frame.PixelWidth != Width || frame.PixelHeight != Height) return null;
            frame.Freeze();
            return frame;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or FileFormatException) { return null; }
    }

    /// <summary>Extracts, checks and caches the map. Returns the ROM's label.</summary>
    internal static (BitmapSource Map, string Label) ImportFromRom(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new InvalidOperationException("Fichier introuvable.");
        if (info.Length != 16 * 1024 * 1024) throw new InvalidOperationException("Ce fichier n’est pas une ROM Rouge Feu d’origine (16 Mio attendus).");
        byte[] rom = File.ReadAllBytes(path);
        string sha1 = Convert.ToHexString(SHA1.HashData(rom));
        if (!Roms.TryGetValue(sha1, out var source)) throw new InvalidOperationException("ROM non reconnue. Choisissez la ROM Rouge Feu d’origine, française ou anglaise, celle qu’accepte le lanceur du jeu.");
        var map = Render(rom, source);
        Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
        string temporary = CachePath + ".tmp";
        using (var stream = File.Create(temporary))
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(map));
            encoder.Save(stream);
        }
        File.Move(temporary, CachePath, true);
        return (map, source.Label);
    }

    internal static void Forget() { try { File.Delete(CachePath); } catch (IOException) { } }

    private static BitmapSource Render(byte[] rom, Source source)
    {
        byte[] tiles = Lz77(rom, source.Tiles, 64 * 1024);
        byte[] tilemap = Lz77(rom, source.Tilemap, 4096);
        byte[] palette = rom.AsSpan(source.Palette, 160).ToArray();
        Check(tiles, TilesSha256); Check(tilemap, TilemapSha256); Check(palette, PaletteSha256);
        var colors = new uint[palette.Length / 2];
        for (int i = 0; i < colors.Length; i++)
        {
            int value = palette[2 * i] | palette[2 * i + 1] << 8;
            uint r = (uint)((value & 31) * 255 / 31), g = (uint)((value >> 5 & 31) * 255 / 31), b = (uint)((value >> 10 & 31) * 255 / 31);
            colors[i] = 0xFF000000 | r << 16 | g << 8 | b;
        }
        // The map-switch button drawn in the lower right corner is replaced by
        // the sea tiles of the same columns, a few rows above.
        int Entry(int tx, int ty)
        {
            if (tx is >= 24 and <= 26 && ty is >= 16 and <= 18) ty = 13;
            int at = (ty * 30 + tx) * 2;
            return tilemap[at] | tilemap[at + 1] << 8;
        }
        var pixels = new uint[Width * Height];
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                int sx = x + Left, sy = y + Top;
                int entry = Entry(sx / 8, sy / 8);
                int tile = entry & 0x3FF, bank = entry >> 12;
                int px = sx % 8, py = sy % 8;
                if ((entry & 0x400) != 0) px = 7 - px;
                if ((entry & 0x800) != 0) py = 7 - py;
                int at = tile * 32 + py * 4 + px / 2;
                int index = at < tiles.Length ? (px % 2 == 1 ? tiles[at] >> 4 : tiles[at] & 15) : 0;
                int color = index == 0 ? 0 : bank * 16 + index;
                pixels[y * Width + x] = color < colors.Length ? colors[color] : colors[0];
            }
        }
        var bitmap = BitmapSource.Create(Width, Height, 96, 96, PixelFormats.Bgra32, null, pixels, Width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private static void Check(byte[] data, string sha256)
    {
        if (!string.Equals(Convert.ToHexString(SHA256.HashData(data)), sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("La carte lue dans cette ROM ne correspond pas à celle attendue. Utilisez une ROM d’origine non modifiée.");
    }

    /// <summary>GBA BIOS LZ77 (type 0x10), with every read and copy bounded.</summary>
    internal static byte[] Lz77(byte[] data, int offset, int maxSize)
    {
        if (offset < 0 || offset + 4 > data.Length || data[offset] != 0x10) throw new InvalidOperationException("Données compressées introuvables à l’adresse prévue.");
        int size = data[offset + 1] | data[offset + 2] << 8 | data[offset + 3] << 16;
        if (size <= 0 || size > maxSize) throw new InvalidOperationException("Taille de données inattendue.");
        var output = new byte[size];
        int read = offset + 4, written = 0;
        while (written < size)
        {
            if (read >= data.Length) throw new InvalidOperationException("Données compressées tronquées.");
            int flags = data[read++];
            for (int bit = 0; bit < 8 && written < size; bit++)
            {
                if ((flags & 0x80 >> bit) == 0)
                {
                    if (read >= data.Length) throw new InvalidOperationException("Données compressées tronquées.");
                    output[written++] = data[read++];
                    continue;
                }
                if (read + 1 >= data.Length) throw new InvalidOperationException("Données compressées tronquées.");
                int length = (data[read] >> 4) + 3, distance = ((data[read] & 15) << 8 | data[read + 1]) + 1;
                read += 2;
                if (distance > written) throw new InvalidOperationException("Données compressées invalides.");
                for (int i = 0; i < length && written < size; i++, written++) output[written] = output[written - distance];
            }
        }
        return output;
    }
}
