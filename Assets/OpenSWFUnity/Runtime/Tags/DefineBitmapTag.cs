using UnityEngine;

namespace OpenSWFUnity.Runtime.Tags
{
    // A decoded bitmap character (DefineBitsLossless/2, DefineBits/JPEG*).
    // Pixels are stored straight (non-premultiplied) RGBA in top-down order;
    // the texture is created lazily so parsing stays off the GPU thread.
    public class DefineBitmapTag
    {
        public ushort CharacterId;
        public int Width;
        public int Height;
        public Color32[] Pixels;

        // Set for JPEG-family tags that Unity decodes itself.
        public byte[] EncodedJpeg;
        public byte[] JpegAlpha;

        private Texture2D texture;

        // The quality revision the cached texture was built at. A change in filtering
        // or anisotropy only needs the sampler state refreshed, but a change in the
        // resolution cap needs the texture rebuilt from the decoded pixels.
        private int textureQualityRevision = -1;
        private int textureSizeCap;

        public Texture2D GetTexture()
        {
            Renderer.SwfQualitySettings settings = Renderer.SwfRenderQuality.Settings;

            if (texture != null)
            {
                if (textureQualityRevision != Renderer.SwfRenderQuality.Revision)
                {
                    if (textureSizeCap != settings.MaxTextureSize)
                    {
                        // Rebuild: the cap moved, so the stored texture is the wrong
                        // size for the level now in force.
                        Object.DestroyImmediate(texture);
                        texture = null;
                    }
                    else
                    {
                        ApplySamplerState(settings);
                        textureQualityRevision = Renderer.SwfRenderQuality.Revision;
                    }
                }

                if (texture != null)
                    return texture;
            }

            if (EncodedJpeg != null)
            {
                texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);

                if (!texture.LoadImage(EncodedJpeg, false))
                {
                    Object.Destroy(texture);
                    texture = null;
                    return null;
                }

                ApplyJpegAlpha();
            }
            else
            {
                if (Pixels == null || Width <= 0 || Height <= 0)
                    return null;

                texture = new Texture2D(Width, Height, TextureFormat.RGBA32, false);
                texture.SetPixels32(Pixels);
                texture.Apply(false, false);
            }

            texture = DownscaleToCap(texture, settings.MaxTextureSize);
            textureSizeCap = settings.MaxTextureSize;
            textureQualityRevision = Renderer.SwfRenderQuality.Revision;

            texture.wrapMode = TextureWrapMode.Clamp;
            texture.hideFlags = HideFlags.HideAndDontSave;
            ApplySamplerState(settings);
            return texture;
        }

        private void ApplySamplerState(Renderer.SwfQualitySettings settings)
        {
            if (texture == null)
                return;

            texture.filterMode = settings.BitmapFilter;
            texture.anisoLevel = Mathf.Clamp(settings.BitmapAnisoLevel, 0, 16);
        }

        // Halves the texture until it fits the quality level's cap. Done once, at
        // decode time, so the reduced texture is what is stored and uploaded rather
        // than the full-size original being kept alive alongside it.
        private static Texture2D DownscaleToCap(Texture2D source, int cap)
        {
            if (source == null || cap <= 0)
                return source;

            int width = source.width;
            int height = source.height;

            if (width <= cap && height <= cap)
                return source;

            while ((width > cap || height > cap) && width > 1 && height > 1)
            {
                width = Mathf.Max(1, width / 2);
                height = Mathf.Max(1, height / 2);
            }

            Color32[] sourcePixels = source.GetPixels32();
            Color32[] scaled = new Color32[width * height];
            int sourceWidth = source.width;
            int sourceHeight = source.height;

            for (int y = 0; y < height; y++)
            {
                int sourceY = Mathf.Min(sourceHeight - 1, y * sourceHeight / height);
                int sourceRow = sourceY * sourceWidth;
                int targetRow = y * width;

                for (int x = 0; x < width; x++)
                {
                    int sourceX = Mathf.Min(sourceWidth - 1, x * sourceWidth / width);
                    scaled[targetRow + x] = sourcePixels[sourceRow + sourceX];
                }
            }

            Texture2D reduced = new Texture2D(width, height, TextureFormat.RGBA32, false);
            reduced.SetPixels32(scaled);
            reduced.Apply(false, false);
            Object.DestroyImmediate(source);
            return reduced;
        }

        // DefineBitsJPEG3 carries its alpha channel separately from the JPEG.
        private void ApplyJpegAlpha()
        {
            if (JpegAlpha == null || texture == null)
                return;

            Color32[] pixels = texture.GetPixels32();

            if (JpegAlpha.Length < pixels.Length)
                return;

            for (int i = 0; i < pixels.Length; i++)
                pixels[i].a = JpegAlpha[i];

            texture.SetPixels32(pixels);
            texture.Apply(false, false);
        }

        public override string ToString()
        {
            return $"Bitmap Id={CharacterId} {Width}x{Height}";
        }
    }
}
