#if BeatSaber
extern alias BeatSaber;
using BeatSaber::UnityEngine;
using System;
using System.Collections;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using IPA.Loader;
using Graphics = System.Drawing.Graphics;
using System.Linq;
using System.Reflection;
using System.Threading;

namespace BeatSaberPlaylistsLib
{
    public static partial class Utilities
    {
        private static Lazy<Sprite?> _defaultSpriteLoader = new Lazy<Sprite?>(() =>
        {
            Logger?.Invoke("Loading default sprite.", null);
            using Stream? stream = GetDefaultImageStream();
            if (stream == null)
                throw new InvalidOperationException("Couldn't get image stream from resources.");
            Logger?.Invoke($"Manifest stream is {stream.Length} bytes.", null);
            return GetSpriteFromStream(stream, 100.0f, false);
        });
        /// <summary>
        /// Default playlist cover, loaded on first access.
        /// </summary>
        public static Sprite? DefaultSprite => _defaultSpriteLoader.Value;
        /// <summary>
        /// Logger for debugging sprite loads.
        /// </summary>
        public static Action<string?, Exception?>? Logger;
        /// <summary>
        /// Creates a <see cref="Sprite"/> from an image <see cref="Stream"/>.
        /// </summary>
        /// <param name="imageStream"></param>
        /// <param name="pixelsPerUnit"></param>
        /// <param name="returnDefaultOnFail"></param>
        /// <returns></returns>
        public static Sprite? GetSpriteFromStream(Stream imageStream, float pixelsPerUnit = 100.0f, bool returnDefaultOnFail = true)
        {
            Texture2D? texture = null;
            Sprite? ReturnDefault(bool useDefault)
            {
                if (useDefault)
                    return DefaultSprite;
                return null;
            }
            try
            {
                Logger?.Invoke($"imageStream is {imageStream?.Length ?? -1} bytes.", null);
                if (imageStream == null || (imageStream.CanSeek && imageStream.Length == 0))
                {
                    //Logger?.Invoke($"imageStream seems to be null or empty.", null);
                    return ReturnDefault(returnDefaultOnFail) ?? throw new ArgumentNullException(nameof(imageStream));
                }
                byte[]? data = null;
                if (imageStream is MemoryStream memStream)
                {
                    //Logger?.Invoke($"imageStream is a MemoryStream", null);
                    data = memStream.ToArray();
                }
                else
                {
                    data = imageStream.ToArray();
                }
                //Logger?.Invoke($"data is {data?.Length ?? -1} bytes long.", null);
                if (data == null || data.Length == 0)
                {
                    //Logger?.Invoke($"data seems to be null or empty.", null);
                    return ReturnDefault(returnDefaultOnFail);
                }
                texture = new Texture2D(2, 2);
                if (!texture.LoadImage(data))
                    return ReturnDefault(returnDefaultOnFail);
                var sprite = Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), new Vector2(0, 0), pixelsPerUnit);
                if (sprite != null)
                    texture = null;
                return sprite ?? ReturnDefault(returnDefaultOnFail);
            }
            catch (Exception ex)
            {
                Logger?.Invoke($"Caught unhandled exception", ex);
                return ReturnDefault(returnDefaultOnFail);
            }
            finally
            {
                if (texture != null)
                    BeatSaber::UnityEngine.Object.Destroy(texture);
            }

        }

        /// <summary>
        /// Gets a <see cref="Stream"/> from a <see cref="Sprite"/>
        /// </summary>
        /// <param name="beatmapLevel"></param>
        /// <returns></returns>
        public static Stream? GetStreamFromBeatmap(BeatSaber::BeatmapLevel? beatmapLevel)
        {
            if (beatmapLevel != null && !beatmapLevel.hasPrecalculatedData && SongCore.Loader.CustomLevelLoader != null)
            {
                var saveDataField = typeof(BeatSaber::CustomLevelLoader).GetField("_loadedBeatmapSaveData", BindingFlags.Instance | BindingFlags.NonPublic);
                var saveData = saveDataField?.GetValue(SongCore.Loader.CustomLevelLoader) as IDictionary;
                var loadedSaveData = saveData?[beatmapLevel.levelID];
                var standardLevelInfoSaveData = loadedSaveData?.GetType().GetField("standardLevelInfoSaveData")?.GetValue(loadedSaveData);
                var customLevelFolderInfo = loadedSaveData?.GetType().GetField("customLevelFolderInfo")?.GetValue(loadedSaveData);
                var fileName = standardLevelInfoSaveData?.GetType().GetProperty("coverImageFilename")?.GetValue(standardLevelInfoSaveData) as string;
                var customLevelPath = customLevelFolderInfo?.GetType().GetField("folderPath")?.GetValue(customLevelFolderInfo) as string;
                if (!string.IsNullOrEmpty(fileName))
                {
                    return new FileStream(Path.Combine(customLevelPath, fileName), FileMode.Open, FileAccess.Read, FileShare.Read, 0x4096, true);
                }
            }
            return GetDefaultImageStream();
        }

        /// <summary>
        /// Downscales <paramref name="original"/> to <paramref name="imageSize"/>
        /// </summary>
        /// <param name="original"></param>
        /// <param name="imageSize"></param>
        /// <returns></returns>
        public static Stream DownscaleImage(Stream original, int imageSize)
        {
            MemoryStream? resizedStream = null;
            long originalPosition = original.CanSeek ? original.Position : 0;
            try
            {
                using var originalImage = Image.FromStream(original);

                if (originalImage.Width <= imageSize && originalImage.Height <= imageSize)
                {
                    if (original.CanSeek)
                        original.Position = originalPosition;
                    return original;
                }

                var resizedRect = new Rectangle(0, 0, imageSize, imageSize);
                using var resizedImage = new Bitmap(imageSize, imageSize);

                resizedImage.SetResolution(originalImage.HorizontalResolution, originalImage.VerticalResolution);

                using (var graphics = Graphics.FromImage(resizedImage))
                {
                    using var wrapMode = new ImageAttributes();
                    wrapMode.SetWrapMode(System.Drawing.Drawing2D.WrapMode.TileFlipXY);
                    graphics.DrawImage(originalImage, resizedRect, 0, 0, originalImage.Width, originalImage.Height, GraphicsUnit.Pixel, wrapMode);
                }

                resizedStream = new MemoryStream();
                resizedImage.Save(resizedStream, ImageFormat.Png);
                resizedStream.Position = 0;
                return resizedStream;
            }
            catch (Exception)
            {
                resizedStream?.Dispose();
                if (original.CanSeek)
                    original.Position = originalPosition;
                return original;
            }
        }

        internal static bool ImageSharpLoaded()
        {
            var imageSharp = PluginManager.GetPluginFromId("SixLabors.ImageSharp");
            return imageSharp != null;
        }
    }
}
#endif
