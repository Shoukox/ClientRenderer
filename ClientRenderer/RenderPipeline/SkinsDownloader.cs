using ClientRenderer.Abstractions;
using ClientRenderer.Logging;
using ClientRenderer.Models;
using DanserWrapper;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace ClientRenderer.RenderPipeline
{
    public class SkinsDownloader : ISkinsDownloader
    {
        public async Task<bool> DownloadSkin(RenderPipelineInfo info, IServerConnection serverConnection)
        {
            string skinNameNoOsk = ToStableHash(info.RenderJob.RenderSettings.SkinName[..^4]);
            string oskPath = Path.Combine(AppContext.BaseDirectory, skinNameNoOsk);
            info.RenderJob.RenderSettings.Encoder = info.ChosenRenderingEncoder;
            if (info.RenderJob.RenderSettings.SkinName.EndsWith(".osk"))
            {
                string skinDirectory = Path.Combine(DanserGo.DanserGoDirectoryPath, "skins", skinNameNoOsk);
                if (!Directory.Exists(skinDirectory))
                {
                    string skinNameHex = Convert.ToHexString(Encoding.ASCII.GetBytes(info.RenderJob.RenderSettings.SkinName)) + ".osk";
                    Logger.Log($"[JobId:{info.RenderJob!.JobId}] Skin: {info.RenderJob.RenderSettings.SkinName}. Downloading a skin...");
                    Stream skinAsStream = new MemoryStream(await serverConnection.DownloadSkin(skinNameHex));
                    if (info.UseExperimentalRenderer)
                    {
                        using var fs = new FileStream(oskPath, FileMode.Create, FileAccess.Write);
                        await skinAsStream.CopyToAsync(fs);
                        skinAsStream.Position = 0;
                    }
                    ZipFile.ExtractToDirectory(skinAsStream, skinDirectory);
                }
                else
                {
                    if (info.UseExperimentalRenderer)
                    {
                        if (File.Exists(oskPath))
                        {
                            File.Delete(oskPath);
                        }
                        ZipFile.CreateFromDirectory(skinDirectory, oskPath);
                    }
                    Logger.Log($"[JobId:{info.RenderJob!.JobId}] Skin: {info.RenderJob.RenderSettings.SkinName}. Already exists.");
                }
                info.HashedSkinName = skinNameNoOsk;
            }

            info.SkinOskPath = oskPath;
            return true;
        }

        private static string ToStableHash(string value)
        {
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
            return Convert.ToHexString(hash)[..16].ToLowerInvariant();
        }
    }
}
