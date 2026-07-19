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

        public Texture2D GetTexture()
        {
            if (texture != null)
                return texture;

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

            texture.wrapMode = TextureWrapMode.Clamp;
            texture.filterMode = FilterMode.Bilinear;
            texture.hideFlags = HideFlags.HideAndDontSave;
            return texture;
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
