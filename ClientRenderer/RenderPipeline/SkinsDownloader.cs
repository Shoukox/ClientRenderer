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
            try
            {
                string skinNameNoOsk = ToStableHash(info.RenderJob.RenderSettings.SkinName[..^4]);
                string oskPath = Path.Combine(AppContext.BaseDirectory, skinNameNoOsk);
                info.RenderJob.RenderSettings.Encoder = info.ChosenRenderingEncoder;
                Logger.Log($"[JobId:{info.RenderJob!.JobId}] Using renderer encoder: {info.ChosenRenderingEncoder}");
                if (info.RenderJob.RenderSettings.SkinName.EndsWith(".osk"))
                {
                    string skinDirectory = Path.Combine(DanserGo.DanserGoDirectoryPath, "skins", skinNameNoOsk);
                    if (!Directory.Exists(skinDirectory))
                    {
                        string skinNameHex = Convert.ToHexString(Encoding.ASCII.GetBytes(info.RenderJob.RenderSettings.SkinName)) + ".osk";
                        Logger.Log($"[JobId:{info.RenderJob!.JobId}] Skin: {info.RenderJob.RenderSettings.SkinName}. Downloading skin...");
                        Stream skinAsStream = new MemoryStream(await serverConnection.DownloadSkin(skinNameHex));
                        if (info.UseExperimentalRenderer)
                        {
                            using FileStream fs = new FileStream(oskPath, FileMode.Create, FileAccess.Write);
                            await skinAsStream.CopyToAsync(fs);
                            skinAsStream.Position = 0;
                        }
                        ZipFile.ExtractToDirectory(skinAsStream, skinDirectory);
                        Logger.Log($"[JobId:{info.RenderJob!.JobId}] Skin extracted to: {skinDirectory}");
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
                            Logger.Log($"[JobId:{info.RenderJob!.JobId}] Skin archive prepared for experimental renderer: {oskPath}");
                        }
                        Logger.Log($"[JobId:{info.RenderJob!.JobId}] Skin already exists locally: {info.RenderJob.RenderSettings.SkinName}");
                    }
                    info.HashedSkinName = skinNameNoOsk;
                }

                info.SkinOskPath = oskPath;
                return true;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, $"[JobId:{info.RenderJob!.JobId}] Failed to prepare skin: {info.RenderJob.RenderSettings.SkinName}");
                throw;
            }
        }

        private static string ToStableHash(string value)
        {
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
            return Convert.ToHexString(hash)[..16].ToLowerInvariant();
        }
    }
}
