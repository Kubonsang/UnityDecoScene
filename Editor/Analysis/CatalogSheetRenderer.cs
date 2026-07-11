using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace UnityDecoScene.DungeonDecorator.Editor
{
    public static class CatalogSheetRenderer
    {
        public static string Render(DecorCatalog catalog, int cellSize = 192, int columns = 4)
        {
            if (catalog == null) throw new ArgumentNullException(nameof(catalog));
            columns = Mathf.Max(1, columns);
            cellSize = Mathf.Clamp(cellSize, 96, 512);
            var count = Mathf.Max(1, catalog.Assets.Count);
            var rows = Mathf.CeilToInt(count / (float)columns);
            var sheet = new Texture2D(columns * cellSize, rows * cellSize, TextureFormat.RGBA32, false, false);
            Fill(sheet, new Color32(28, 31, 36, 255));

            for (var index = 0; index < catalog.Assets.Count; index++)
            {
                var descriptor = catalog.Assets[index];
                if (descriptor == null || descriptor.Prefab == null) continue;
                var preview = AssetPreview.GetAssetPreview(descriptor.Prefab) ?? AssetPreview.GetMiniThumbnail(descriptor.Prefab);
                if (preview == null) continue;
                if (!preview.isReadable) continue;

                var row = rows - 1 - (index / columns);
                var column = index % columns;
                BlitScaled(preview, sheet, column * cellSize, row * cellSize, cellSize, cellSize);
            }

            sheet.Apply(false, false);
            var directory = Path.GetFullPath(Path.Combine(Application.dataPath, "../Library/DungeonDecorator/Catalogs"));
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"catalog-{catalog.GetInstanceID()}-{DateTime.UtcNow:yyyyMMdd-HHmmss}.png");
            File.WriteAllBytes(path, sheet.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(sheet);
            return path;
        }

        private static void Fill(Texture2D target, Color32 color)
        {
            var pixels = new Color32[target.width * target.height];
            for (var i = 0; i < pixels.Length; i++) pixels[i] = color;
            target.SetPixels32(pixels);
        }

        private static void BlitScaled(Texture2D source, Texture2D target, int x, int y, int width, int height)
        {
            var pixels = source.GetPixels32();
            for (var py = 0; py < height; py++)
            for (var px = 0; px < width; px++)
            {
                var sx = Mathf.Clamp(px * source.width / width, 0, source.width - 1);
                var sy = Mathf.Clamp(py * source.height / height, 0, source.height - 1);
                target.SetPixel(x + px, y + py, pixels[sy * source.width + sx]);
            }
        }
    }
}
