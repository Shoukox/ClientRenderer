using DanserWrapper;
using OsuParsers.Decoders;
using OsuParsers.Replays;

namespace ClientRenderer;

public static class ReplaysService
{
    private static List<string> BeatmapsMd5Hashes = new();

    private static readonly object _locker = new();

    public static void LoadAllBeatmapsHashes()
    {
        lock (_locker)
        {
            BeatmapsMd5Hashes = new();
            foreach (string dir in Directory.GetDirectories(DanserGo.SongsPath))
            {
                var beatmaps = Directory.GetFiles(dir).Where(m => m.EndsWith(".osu"));
                foreach (string beatmap in beatmaps)
                {
                    BeatmapsMd5Hashes.Add(CreateMd5(beatmap).GetAwaiter().GetResult().ToLowerInvariant());
                }
            }
        }
    }

    public static string GetBeatmapMd5HashFromReplay(byte[] replayBytes)
    {
        return ReplayDecoder.Decode(new MemoryStream(replayBytes)).BeatmapMD5Hash;
    }

    public static bool BeatmapExists(string beatmapHash)
    {
        bool exists;
        lock (_locker)
        {
            exists = BeatmapsMd5Hashes.Any(m => m.Equals(beatmapHash, StringComparison.InvariantCultureIgnoreCase));
        }

        return exists;
    }

    public static async Task<string> CreateMd5(string path)
    {
        byte[] inputBytes = await File.ReadAllBytesAsync(path);
        return await CreateMd5(inputBytes);
    }

    public static async Task<string> CreateMd5(byte[] bytes)
    {
        byte[] inputBytes = bytes;
        
        using System.Security.Cryptography.MD5 md5 = System.Security.Cryptography.MD5.Create();
        byte[] hashBytes = md5.ComputeHash(inputBytes);
        return Convert.ToHexString(hashBytes);
    }
}