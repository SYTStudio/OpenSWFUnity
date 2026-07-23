using System;
using System.IO;
using System.IO.Compression;
using System.Collections.Generic;
using System.Text;
using OpenSWFUnity.Runtime.Tags;
using UnityEngine;

namespace OpenSWFUnity.Runtime.Parser
{
    public class SwfParser
    {
        private byte[] data;
        private int position;
        public bool VerboseLogging = false;

        // Snapshotted when parsing begins so a quality change mid-parse cannot
        // produce a shape library built at two different fidelities.
        private int curveSubdivisionSteps =
            Renderer.SwfRenderQuality.Settings.CurveSubdivisionSteps;
        public bool DiagnosticsLogging = true;

        public SwfHeader Header { get; private set; }

        public List<SwfTag> Tags { get; private set; } = new List<SwfTag>();

        public System.Collections.Generic.List<DefineBitmapTag> Bitmaps { get; private set; } = new System.Collections.Generic.List<DefineBitmapTag>();
        public byte[] JpegTables { get; private set; }

        public System.Collections.Generic.List<DefineShapeTag> Shapes { get; private set; } = new System.Collections.Generic.List<DefineShapeTag>();
        public bool LazyShapeParsing { get; set; } = true;
        private readonly Dictionary<ushort, DeferredShapeInfo> deferredShapes =
            new Dictionary<ushort, DeferredShapeInfo>();
        public System.Collections.Generic.List<PlaceObject2Tag> PlacedObjects { get; private set; } = new System.Collections.Generic.List<PlaceObject2Tag>();
        public System.Collections.Generic.List<DefineSpriteTag> Sprites { get; private set; } = new System.Collections.Generic.List<DefineSpriteTag>();
        public System.Collections.Generic.List<DefineTextTag> Texts { get; private set; } = new System.Collections.Generic.List<DefineTextTag>();
        public List<DefineEditTextTag> EditTexts { get; private set; } = new List<DefineEditTextTag>();
        public System.Collections.Generic.List<DefineFont3Tag> Fonts3 { get; private set; } = new System.Collections.Generic.List<DefineFont3Tag>();
        public List<DefineButton2Tag> Buttons2 { get; private set; } = new List<DefineButton2Tag>();
        public List<DefineSoundTag> Sounds { get; private set; } = new List<DefineSoundTag>();

        // Streaming audio. The head declares the format once; the blocks arrive one
        // per timeline frame, which is what allows the stream to be aligned with the
        // playhead rather than merely started alongside it.
        public SwfSoundStreamHead SoundStreamHead { get; private set; }
        public List<SwfSoundStreamBlock> SoundStreamBlocks { get; private set; } =
            new List<SwfSoundStreamBlock>();

        // Formats encountered that this build cannot decode, reported once each.
        public readonly HashSet<int> UnsupportedSoundFormats = new HashSet<int>();
        public Dictionary<string, ushort> ExportedAssets { get; private set; } = new Dictionary<string, ushort>();
        public List<SwfFrame> RootFrames { get; private set; } = new List<SwfFrame>();
        // SWF 9+ may store root timeline labels in
        // DefineSceneAndFrameLabelData (tag 86) instead of individual FrameLabel
        // tags. Values are zero-based frame indices, matching the tag format.
        public Dictionary<string, int> RootFrameLabels { get; private set; } =
            new Dictionary<string, int>(StringComparer.Ordinal);
        public List<SwfInitAction> InitActions { get; private set; } = new List<SwfInitAction>();
        public List<SwfDoAbcBlock> DoAbcBlocks { get; private set; } = new List<SwfDoAbcBlock>();

        public SetBackgroundColorTag BackgroundColor { get; private set; }

        // ScriptLimits (tag 65). Present only when the movie ships the tag; the
        // runtime uses these to raise (never lower) its safety guards.
        public bool HasScriptLimits { get; private set; }
        public ushort ScriptMaxRecursionDepth { get; private set; }
        public ushort ScriptTimeoutSeconds { get; private set; }

        // File-level declarations and descriptive metadata. These tags do not draw
        // anything themselves, but exposing them keeps the parser honest and lets
        // the runtime choose the correct VM/security behaviour without guessing.
        public bool HasFileAttributes { get; private set; }
        public bool UsesDirectBlit { get; private set; }
        public bool UsesGpu { get; private set; }
        public bool DeclaresMetadata { get; private set; }
        public bool DeclaresActionScript3 { get; private set; }
        public bool UsesNetwork { get; private set; }
        public string MetadataXml { get; private set; }
        public Dictionary<ushort, string> FontDisplayNames { get; private set; } =
            new Dictionary<ushort, string>();
        public Dictionary<ushort, SwfFontAlignZones> FontAlignZones { get; private set; } =
            new Dictionary<ushort, SwfFontAlignZones>();
        public Dictionary<ushort, SwfCsmTextSettings> CsmTextSettings { get; private set; } =
            new Dictionary<ushort, SwfCsmTextSettings>();


        public int DefineFontCount;
        public int DefineFont2Count;
        public int DefineFont3Count;
        public int DefineTextCount;
        public int DefineText2Count;
        public int DefineEditTextCount;
        public int CsmTextSettingsCount;
        public int FontAlignZonesCount;
        public int DefineMorphShapeCount;
        public int DefineMorphShape2Count;

        public SwfParser(byte[] swfBytes)
        {
            data = swfBytes;
            position = 0;
        }

        public SwfHeader ParseHeader()
        {
            if (data == null || data.Length < 8)
                throw new Exception("Invalid SWF file. File is too small.");

            char sig1 = (char)data[0];
            char sig2 = (char)data[1];
            char sig3 = (char)data[2];

            string signature = $"{sig1}{sig2}{sig3}";
            byte version = data[3];
            uint fileLength = ReadUInt32LE(4);

            if (signature == "CWS")
            {
                data = DecompressCws(data);
                signature = "FWS";
            }
            else if (signature != "FWS")
            {
                throw new Exception("Unsupported SWF signature: " + signature);
            }
            position = 8;

            RectInfo rect = ReadRect(position);

            position = rect.NextBytePosition;

            ushort frameRateRaw = ReadUInt16LE(position);
            position += 2;

            ushort frameCount = ReadUInt16LE(position);
            position += 2;

            Header = new SwfHeader
            {
                Signature = signature,
                Version = version,
                FileLength = fileLength,
                StageWidth = rect.WidthTwips / 20f,
                StageHeight = rect.HeightTwips / 20f,
                FrameRate = frameRateRaw / 256f,
                FrameCount = frameCount
            };

            return Header;
        }

        private RectInfo ReadRect(int offset)
        {
            SwfBitReader reader = new SwfBitReader(data, offset);

            int nbits = (int)reader.ReadUBits(5);

            int xmin = reader.ReadSBits(nbits);
            int xmax = reader.ReadSBits(nbits);
            int ymin = reader.ReadSBits(nbits);
            int ymax = reader.ReadSBits(nbits);

            reader.AlignToByte();

            return new RectInfo
            {
                XMin = xmin,
                XMax = xmax,
                YMin = ymin,
                YMax = ymax,
                NextBytePosition = reader.BytePosition
            };
        }

        private ushort ReadUInt16LE(int offset)
        {
            return BitConverter.ToUInt16(data, offset);
        }

        private uint ReadUInt32LE(int offset)
        {
            return BitConverter.ToUInt32(data, offset);
        }

        private static byte[] InflateZlib(byte[] source, int offset, int length)
        {
            if (source == null || offset < 0 || length <= 0 || offset + length > source.Length)
                return new byte[0];

            // Skip the 2-byte zlib header when present; DeflateStream wants raw deflate.
            int start = offset;
            int count = length;

            if (count > 2 && source[start] == 0x78)
            {
                start += 2;
                count -= 2;
            }

            using MemoryStream input = new MemoryStream(source, start, count);
            using DeflateStream deflate = new DeflateStream(input, CompressionMode.Decompress);
            using MemoryStream output = new MemoryStream();
            deflate.CopyTo(output);
            return output.ToArray();
        }

        private void ParseDefineBitsLossless(SwfTag tag, bool hasAlpha)
        {
            try
            {
                int p = tag.DataStart;
                ushort characterId = ReadUInt16LEAt(p); p += 2;
                byte format = data[p]; p += 1;
                int width = ReadUInt16LEAt(p); p += 2;
                int height = ReadUInt16LEAt(p); p += 2;

                int colorTableSize = 0;

                if (format == 3)
                {
                    colorTableSize = ReadByteAt(p) + 1;
                    p += 1;
                }

                if (width <= 0 || height <= 0)
                    return;

                int remaining = tag.DataStart + tag.Length - p;
                byte[] pixelData = InflateZlib(data, p, remaining);

                if (pixelData.Length == 0)
                    return;

                Color32[] pixels = new Color32[width * height];
                int cursor = 0;

                if (format == 3)
                {
                    // Palette entries are RGB for Lossless and RGBA for Lossless2.
                    int entrySize = hasAlpha ? 4 : 3;
                    Color32[] palette = new Color32[colorTableSize];

                    for (int i = 0; i < colorTableSize && cursor + entrySize <= pixelData.Length; i++)
                    {
                        byte r = pixelData[cursor];
                        byte g = pixelData[cursor + 1];
                        byte b = pixelData[cursor + 2];
                        byte a = hasAlpha ? pixelData[cursor + 3] : (byte)255;
                        cursor += entrySize;
                        palette[i] = UnmultiplyAlpha(r, g, b, a, hasAlpha);
                    }

                    // Each row of indices is padded out to a 32-bit boundary.
                    int rowStride = (width + 3) & ~3;

                    for (int y = 0; y < height; y++)
                    {
                        int rowStart = cursor + y * rowStride;

                        for (int x = 0; x < width; x++)
                        {
                            int offset = rowStart + x;
                            int index = offset < pixelData.Length ? pixelData[offset] : 0;
                            pixels[y * width + x] = index < palette.Length
                                ? palette[index]
                                : new Color32(0, 0, 0, 0);
                        }
                    }
                }
                else if (format == 4)
                {
                    // PIX15: 16-bit, one padding bit then 5 bits per channel.
                    int rowStride = ((width * 2) + 3) & ~3;

                    for (int y = 0; y < height; y++)
                    {
                        int rowStart = y * rowStride;

                        for (int x = 0; x < width; x++)
                        {
                            int offset = rowStart + x * 2;

                            if (offset + 1 >= pixelData.Length)
                                continue;

                            int value = (pixelData[offset] << 8) | pixelData[offset + 1];
                            byte r = (byte)(((value >> 10) & 0x1F) * 255 / 31);
                            byte g = (byte)(((value >> 5) & 0x1F) * 255 / 31);
                            byte b = (byte)((value & 0x1F) * 255 / 31);
                            pixels[y * width + x] = new Color32(r, g, b, 255);
                        }
                    }
                }
                else
                {
                    // Format 5: 32 bits per pixel. Lossless stores XRGB (X unused),
                    // Lossless2 stores ARGB with colours already premultiplied.
                    for (int i = 0; i < width * height; i++)
                    {
                        int offset = i * 4;

                        if (offset + 3 >= pixelData.Length)
                            break;

                        byte a = hasAlpha ? pixelData[offset] : (byte)255;
                        byte r = pixelData[offset + 1];
                        byte g = pixelData[offset + 2];
                        byte b = pixelData[offset + 3];
                        pixels[i] = UnmultiplyAlpha(r, g, b, a, hasAlpha);
                    }
                }

                Bitmaps.Add(new DefineBitmapTag
                {
                    CharacterId = characterId,
                    Width = width,
                    Height = height,
                    Pixels = pixels
                });
            }
            catch (Exception e)
            {
                if (DiagnosticsLogging)
                    Debug.LogWarning("Failed to parse DefineBitsLossless: " + e.Message);
            }
        }

        private static Color32 UnmultiplyAlpha(byte r, byte g, byte b, byte a, bool premultiplied)
        {
            if (!premultiplied || a == 0 || a == 255)
                return new Color32(r, g, b, a);

            return new Color32(
                (byte)Mathf.Min(255, r * 255 / a),
                (byte)Mathf.Min(255, g * 255 / a),
                (byte)Mathf.Min(255, b * 255 / a),
                a
            );
        }

        private void ParseDefineBitsJpeg(SwfTag tag, int tagCode)
        {
            try
            {
                int p = tag.DataStart;
                ushort characterId = ReadUInt16LEAt(p); p += 2;
                int end = tag.DataStart + tag.Length;
                byte[] alpha = null;
                int jpegEnd = end;

                if (tagCode == 35 || tagCode == 90)
                {
                    // JPEG3/4 prefix the image with its own length; the remainder
                    // of the tag is a zlib-compressed alpha channel.
                    uint jpegLength = ReadUInt32LEAt(p); p += 4;

                    if (tagCode == 90)
                        p += 2; // DefineBitsJPEG4 deblocking parameter

                    jpegEnd = Math.Min(end, p + (int)jpegLength);

                    if (jpegEnd < end)
                        alpha = InflateZlib(data, jpegEnd, end - jpegEnd);
                }

                int jpegLen = Math.Max(0, jpegEnd - p);

                if (jpegLen <= 0)
                    return;

                byte[] jpeg;

                if (tagCode == 6 && JpegTables != null && JpegTables.Length > 2)
                {
                    // DefineBits shares its encoding tables with the JPEGTables tag;
                    // splice them together, dropping the tables' trailing EOI marker
                    // and the image's leading SOI marker.
                    byte[] merged = new byte[JpegTables.Length - 2 + jpegLen - 2];
                    Array.Copy(JpegTables, 0, merged, 0, JpegTables.Length - 2);
                    Array.Copy(data, p + 2, merged, JpegTables.Length - 2, jpegLen - 2);
                    jpeg = merged;
                }
                else
                {
                    jpeg = new byte[jpegLen];
                    Array.Copy(data, p, jpeg, 0, jpegLen);
                }

                Bitmaps.Add(new DefineBitmapTag
                {
                    CharacterId = characterId,
                    EncodedJpeg = jpeg,
                    JpegAlpha = alpha
                });
            }
            catch (Exception e)
            {
                if (DiagnosticsLogging)
                    Debug.LogWarning("Failed to parse DefineBitsJPEG: " + e.Message);
            }
        }

        public DefineBitmapTag FindBitmapById(ushort id)
        {
            if (bitmapIndex == null)
            {
                bitmapIndex = new Dictionary<ushort, DefineBitmapTag>(Bitmaps.Count);
                for (int i = 0; i < Bitmaps.Count; i++)
                    bitmapIndex[Bitmaps[i].CharacterId] = Bitmaps[i];
            }

            return bitmapIndex.TryGetValue(id, out DefineBitmapTag bitmap) ? bitmap : null;
        }

        private byte[] DecompressCws(byte[] compressedSwf)
        {
            if (compressedSwf == null || compressedSwf.Length < 10)
                throw new Exception("Invalid CWS file. File too small.");

            byte[] header = new byte[8];
            Array.Copy(compressedSwf, 0, header, 0, 8);

            // CWS -> FWS
            header[0] = (byte)'F';

            uint uncompressedLength = BitConverter.ToUInt32(compressedSwf, 4);

            using MemoryStream output = new MemoryStream();

            output.Write(header, 0, 8);

            // SWF CWS data starts at byte 8.
            // First 2 bytes after byte 8 are usually zlib header.
            int compressedOffset = 8;

            bool hasZlibHeader = compressedSwf.Length > 10 &&
                                 compressedSwf[8] == 0x78;

            if (hasZlibHeader)
            {
                compressedOffset += 2;
            }

            using MemoryStream compressedStream = new MemoryStream(
                compressedSwf,
                compressedOffset,
                compressedSwf.Length - compressedOffset
            );

            using DeflateStream deflateStream = new DeflateStream(
                compressedStream,
                CompressionMode.Decompress
            );

            deflateStream.CopyTo(output);

            byte[] result = output.ToArray();

            if (DiagnosticsLogging && result.Length != uncompressedLength)
            {
                Debug.LogWarning(
                    "CWS decompressed size mismatch. Expected: " +
                    uncompressedLength +
                    " Got: " +
                    result.Length
                );
            }

            return result;
        }

        public List<SwfTag> ParseTags()
        {
            if (Header == null)
                ParseHeader();

            curveSubdivisionSteps =
                System.Math.Max(1, Renderer.SwfRenderQuality.Settings.CurveSubdivisionSteps);
            Renderer.SwfRenderQuality.MarkParsed();

            Tags.Clear();
            RootFrames.Clear();

            SwfFrame currentRootFrame = new SwfFrame
            {
                FrameIndex = 0
            };

            while (position < data.Length)
            {
                int tagStart = position;

                ushort tagCodeAndLength = ReadUInt16LE(position);
                position += 2;

                int tagCode = tagCodeAndLength >> 6;
                int tagLength = tagCodeAndLength & 0x3F;

                if (tagLength == 0x3F)
                {
                    tagLength = (int)ReadUInt32LE(position);
                    position += 4;
                }

                SwfTag tag = new SwfTag
                {
                    Code = tagCode,
                    Length = tagLength,
                    DataStart = position,
                    Name = SwfTagNames.GetName(tagCode)
                };

                Tags.Add(tag);

                if (tagCode == 1) // ShowFrame
                {
                    RootFrames.Add(currentRootFrame);
                    currentRootFrame = new SwfFrame
                    {
                        FrameIndex = RootFrames.Count
                    };
                }
                else if (IsTimelineControlTag(tagCode))
                {
                    currentRootFrame.ControlTags.Add(tag);
                }

                if (VerboseLogging)
                    Debug.Log(tag.ToString());
                ParseKnownTag(tag);

                position += tagLength;

                if (tagCode == 0)
                {
                    break;
                }

                if (position > data.Length)
                {
                    if (DiagnosticsLogging)
                        Debug.LogError("Tag read past end of file at tag: " + tag.Name);
                    break;
                }
            }

            if (currentRootFrame.ControlTags.Count > 0)
            {
                RootFrames.Add(currentRootFrame);
            }

            return Tags;
        }

        private bool IsTimelineControlTag(int tagCode)
        {
            switch (tagCode)
            {
                case 4:  // PlaceObject
                case 5:  // RemoveObject
                case 12: // DoAction
                case 15: // StartSound
                case 18: // SoundStreamHead
                case 19: // SoundStreamBlock
                case 26: // PlaceObject2
                case 28: // RemoveObject2
                case 43: // FrameLabel
                case 45: // SoundStreamHead2
                case 59: // DoInitAction
                case 70: // PlaceObject3
                case 89: // StartSound2
                    return true;

                default:
                    return false;
            }
        }

        private void ParseKnownTag(SwfTag tag)
        {
            if (tag.Code == 9) // SetBackgroundColor
            {
                if (tag.Length >= 3)
                {
                    BackgroundColor = new SetBackgroundColorTag
                    {
                        R = data[tag.DataStart],
                        G = data[tag.DataStart + 1],
                        B = data[tag.DataStart + 2]
                    };

                    if (VerboseLogging)
                        Debug.Log(BackgroundColor.ToString());
                }
            }

            if (tag.Code == 65 && tag.Length >= 4) // ScriptLimits
            {
                HasScriptLimits = true;
                ScriptMaxRecursionDepth = ReadUInt16LEAt(tag.DataStart);
                ScriptTimeoutSeconds = ReadUInt16LEAt(tag.DataStart + 2);

                if (VerboseLogging)
                {
                    Debug.Log(
                        "ScriptLimits MaxRecursionDepth=" + ScriptMaxRecursionDepth +
                        " ScriptTimeoutSeconds=" + ScriptTimeoutSeconds
                    );
                }
            }

            if (tag.Code == 69 && tag.Length >= 4) // FileAttributes
            {
                uint flags = ReadUInt32LEAt(tag.DataStart);
                HasFileAttributes = true;
                UsesNetwork = (flags & 0x01u) != 0;
                DeclaresActionScript3 = (flags & 0x08u) != 0;
                DeclaresMetadata = (flags & 0x10u) != 0;
                UsesGpu = (flags & 0x20u) != 0;
                UsesDirectBlit = (flags & 0x40u) != 0;
            }

            if (tag.Code == 77) // Metadata
            {
                int metadataPosition = tag.DataStart;
                MetadataXml = ReadNullTerminatedString(
                    ref metadataPosition,
                    tag.DataStart + tag.Length);
            }

            if (tag.Code == 59)
            {
                ParseDoInitAction(tag);
            }

            if (tag.Code == 82)
            {
                ParseDoAbc(tag);
            }

            if (tag.Code == 86)
            {
                ParseDefineSceneAndFrameLabelData(tag);
            }

            if (tag.Code == 2 || tag.Code == 22 || tag.Code == 32 || tag.Code == 83)
            {
                ParseDefineShape(tag);
            }

            if (tag.Code == 8) // JPEGTables: shared encoding tables for DefineBits
            {
                JpegTables = CopyTagData(tag);
            }

            if (tag.Code == 20) // DefineBitsLossless (no alpha)
            {
                ParseDefineBitsLossless(tag, false);
            }

            if (tag.Code == 36) // DefineBitsLossless2 (alpha, premultiplied)
            {
                ParseDefineBitsLossless(tag, true);
            }

            if (tag.Code == 6 || tag.Code == 21 || tag.Code == 35 || tag.Code == 90)
            {
                ParseDefineBitsJpeg(tag, tag.Code);
            }

            if (tag.Code == 26 || tag.Code == 70)
            {
                ParsePlaceObject2(tag);
            }

            if (tag.Code == 34)
            {
                ParseDefineButton2(tag);
            }

            if (tag.Code == 14)
            {
                ParseDefineSound(tag);
            }

            if (tag.Code == 18 || tag.Code == 45)
            {
                ParseSoundStreamHead(tag);
            }

            if (tag.Code == 19)
            {
                ParseSoundStreamBlock(tag);
            }

            if (tag.Code == 56)
            {
                ParseExportAssets(tag);
            }

            if (tag.Code == 76)
            {
                ParseExportAssets(tag);
            }

            if (tag.Code == 39)
            {
                ParseDefineSprite(tag);
            }

            if (tag.Code == 11 || tag.Code == 33)
            {
                ParseDefineText(tag);
            }

            if (tag.Code == 37)
            {
                ParseDefineEditText(tag);
            }
            // DefineFont2 and DefineFont3 share the same header, offset table and
            // code-table layout. We currently need the code table to turn static
            // text glyph indices back into Unicode; ignoring tag 48 made SWF8 HUD
            // labels render as "Missing Font" even though the font was embedded.
            if (tag.Code == 48 || tag.Code == 75)
            {
                ParseDefineFont3(tag);
            }

            if (tag.Code == 88)
            {
                ParseDefineFontName(tag);
            }

            if (tag.Code == 73)
            {
                ParseDefineFontAlignZones(tag);
            }

            if (tag.Code == 74)
            {
                ParseCsmTextSettings(tag);
            }

            if (tag.Code == 46)
            {
                DefineMorphShapeCount++;
                ParseDefineMorphShapeFallback(tag);

                if (VerboseLogging)
                {
                    Debug.Log(
                        "Found DefineMorphShape Code=46 Length=" +
                        tag.Length +
                        " DataStart=" +
                        tag.DataStart
                    );
                }
            }

            if (tag.Code == 84)
            {
                DefineMorphShape2Count++;
                ParseDefineMorphShapeFallback(tag);

                if (VerboseLogging)
                {
                    Debug.Log(
                        "Found DefineMorphShape2 Code=84 Length=" +
                        tag.Length +
                        " DataStart=" +
                        tag.DataStart
                    );
                }
            }

            // Text / Font detection
            switch (tag.Code)
            {
                case 10:
                    DefineFontCount++;
                    break;

                case 11:
                    DefineTextCount++;
                    break;

                case 33:
                    DefineText2Count++;
                    break;

                case 37:
                    DefineEditTextCount++;
                    break;

                case 48:
                    DefineFont2Count++;
                    break;

                case 73:
                    FontAlignZonesCount++;
                    break;

                case 74:
                    CsmTextSettingsCount++;
                    break;

                case 75:
                    DefineFont3Count++;
                    break;
            }

            {
                if (VerboseLogging)
                {
                    Debug.Log(
                        "Tag: " + SwfTagNames.GetName(tag.Code) +
                        " Code=" + tag.Code +
                        " Length=" + tag.Length +
                        " DataStart=" + tag.DataStart
                    );
                }
            }
        }

        private ushort ReadUInt16LEAt(int offset)
        {
            return System.BitConverter.ToUInt16(data, offset);
        }

        private SwfRect ReadRectAt(int offset, out int nextBytePosition)
        {
            SwfBitReader reader = new SwfBitReader(data, offset);

            int nbits = (int)reader.ReadUBits(5);

            int xmin = reader.ReadSBits(nbits);
            int xmax = reader.ReadSBits(nbits);
            int ymin = reader.ReadSBits(nbits);
            int ymax = reader.ReadSBits(nbits);

            reader.AlignToByte();

            nextBytePosition = reader.BytePosition;

            return new SwfRect
            {
                XMin = xmin,
                XMax = xmax,
                YMin = ymin,
                YMax = ymax
            };
        }

        public ushort ParseRemoveObject2DepthFromTag(SwfTag tag)
        {
            return ReadUInt16LEAt(tag.DataStart);
        }

        public ushort ParseRemoveObjectDepthFromTag(SwfTag tag)
        {
            return ReadUInt16LEAt(tag.DataStart + 2);
        }

        private void ParseDefineSprite(SwfTag tag)
        {
            try
            {
                int p = tag.DataStart;

                ushort spriteId = ReadUInt16LEAt(p);
                p += 2;

                ushort frameCount = ReadUInt16LEAt(p);
                p += 2;

                DefineSpriteTag sprite = new DefineSpriteTag
                {
                    SpriteId = spriteId,
                    FrameCount = frameCount
                };

                SwfFrame currentFrame = new SwfFrame
                {
                    FrameIndex = 0
                };

                int spriteEnd = tag.DataStart + tag.Length;

                while (p < spriteEnd)
                {
                    ushort tagCodeAndLength = ReadUInt16LEAt(p);
                    p += 2;

                    int innerCode = tagCodeAndLength >> 6;
                    int innerLength = tagCodeAndLength & 0x3F;

                    if (innerLength == 0x3F)
                    {
                        innerLength = (int)ReadUInt32LE(p);
                        p += 4;
                    }

                    SwfTag innerTag = new SwfTag
                    {
                        Code = innerCode,
                        Length = innerLength,
                        DataStart = p,
                        Name = SwfTagNames.GetName(innerCode)
                    };

                    // Keep old flat list for compatibility.
                    sprite.ControlTags.Add(innerTag);

                    if (VerboseLogging)
                    {
                        Debug.Log(
                            $"  Sprite {spriteId} InnerTag: {innerTag.Name} Code={innerTag.Code} Length={innerTag.Length} DataStart={innerTag.DataStart}"
                        );
                    }

                    // End tag closes sprite parsing.
                    if (innerCode == 0) // End
                    {
                        if (currentFrame.ControlTags.Count > 0)
                        {
                            sprite.Frames.Add(currentFrame);
                        }

                        p += innerLength;
                        break;
                    }

                    // ShowFrame closes current frame.
                    if (innerCode == 1) // ShowFrame
                    {
                        sprite.Frames.Add(currentFrame);

                        currentFrame = new SwfFrame
                        {
                            FrameIndex = sprite.Frames.Count
                        };

                        p += innerLength;
                        continue;
                    }

                    // Add normal control tags to current frame.
                    currentFrame.ControlTags.Add(innerTag);

                    if (innerCode == 18 || innerCode == 45)
                    {
                        SwfSoundStreamHead streamHead =
                            ParseSoundStreamHeadFromTag(innerTag);

                        if (streamHead != null &&
                            (streamHead.SamplesPerFrame > 0 || sprite.SoundStreamHead == null))
                        {
                            sprite.SoundStreamHead = streamHead;

                            if (!Audio.SwfSoundFormats.IsSupported(streamHead.StreamFormat))
                                UnsupportedSoundFormats.Add(streamHead.StreamFormat);
                        }
                    }
                    else if (innerCode == 19)
                    {
                        SwfSoundStreamBlock streamBlock =
                            ParseSoundStreamBlockFromTag(
                                innerTag,
                                sprite.SoundStreamHead,
                                sprite.Frames.Count);

                        if (streamBlock != null)
                            sprite.SoundStreamBlocks.Add(streamBlock);
                    }

                    p += innerLength;
                }

                Sprites.Add(sprite);

                if (VerboseLogging)
                {
                    Debug.Log(
                        "Parsed Sprite Timeline. SpriteId=" + sprite.SpriteId +
                        " DeclaredFrames=" + sprite.FrameCount +
                        " ParsedFrames=" + sprite.Frames.Count +
                        " ControlTags=" + sprite.ControlTags.Count
                    );
                }

                if (VerboseLogging)
                {
                    for (int i = 0; i < sprite.Frames.Count; i++)
                    {
                        Debug.Log("  " + sprite.Frames[i].ToString());
                    }
                }
            }
            catch (System.Exception e)
            {
                if (DiagnosticsLogging)
                    Debug.LogWarning("Failed to parse DefineSprite at " + tag.DataStart + ": " + e.Message);
            }
        }

        private void ParseDoInitAction(SwfTag tag)
        {
            if (tag == null || tag.Length < 2)
                return;

            ushort spriteId = ReadUInt16LEAt(tag.DataStart);
            byte[] actionBytes = CopyTagData(tag, 2);
            InitActions.Add(new SwfInitAction
            {
                SpriteId = spriteId,
                ActionBytes = actionBytes
            });

            if (VerboseLogging)
            {
                Debug.Log("Parsed DoInitAction for SpriteId=" + spriteId + " Actions=" + actionBytes.Length);
            }
        }

        private void ParseDoAbc(SwfTag tag)
        {
            if (tag == null || tag.Length < 4)
                return;

            uint flags = ReadUInt32LE(tag.DataStart);
            int p = tag.DataStart + 4;
            int tagEnd = tag.DataStart + tag.Length;
            int nameStart = p;

            while (p < tagEnd && data[p] != 0)
                p++;

            string name = string.Empty;

            if (p > nameStart)
            {
                name = Encoding.UTF8.GetString(data, nameStart, p - nameStart);
            }

            if (p < tagEnd)
                p++;

            int abcLength = Math.Max(0, tagEnd - p);
            byte[] abcData = new byte[abcLength];
            Array.Copy(data, p, abcData, 0, abcLength);

            DoAbcBlocks.Add(new SwfDoAbcBlock
            {
                Flags = flags,
                Name = name,
                AbcData = abcData
            });

            if (DiagnosticsLogging)
            {
                // The block itself is extracted here and parsed by the AVM2 layer;
                // what remains unimplemented is executing the AS3 it contains, which
                // the AVM2 runtime reports once rather than once per block.
                Debug.Log(
                    "Found DoABC block: " +
                    (string.IsNullOrEmpty(name) ? "<anonymous>" : name) +
                    " Flags=0x" + flags.ToString("X8") +
                    " Size=" + abcLength
                );
            }
        }

        private void ParseDefineButton2(SwfTag tag)
        {
            try
            {
                int p = tag.DataStart;
                int tagEnd = tag.DataStart + tag.Length;

                ushort buttonId = ReadUInt16LEAt(p);
                p += 2;

                byte flags = data[p++];
                int actionOffsetField = p;
                ushort actionOffset = ReadUInt16LEAt(p);
                p += 2;

                DefineButton2Tag button = new DefineButton2Tag
                {
                    ButtonId = buttonId,
                    TrackAsMenu = (flags & 0x01) != 0
                };

                int characterEnd = actionOffset == 0
                    ? tagEnd
                    : actionOffsetField + actionOffset;

                while (p < characterEnd)
                {
                    byte recordFlags = data[p++];

                    if (recordFlags == 0)
                        break;

                    SwfButtonRecord record = new SwfButtonRecord
                    {
                        StateHitTest = (recordFlags & 0x08) != 0,
                        StateDown = (recordFlags & 0x04) != 0,
                        StateOver = (recordFlags & 0x02) != 0,
                        StateUp = (recordFlags & 0x01) != 0,
                        HasFilterList = (recordFlags & 0x10) != 0,
                        HasBlendMode = (recordFlags & 0x20) != 0
                    };

                    record.CharacterId = ReadUInt16LEAt(p);
                    p += 2;

                    record.PlaceDepth = ReadUInt16LEAt(p);
                    p += 2;

                    record.Matrix = ReadMatrixAt(p, out int afterMatrix);
                    p = afterMatrix;

                    record.ColorTransform = ReadColorTransformWithAlpha(p, out int afterColorTransform);
                    p = afterColorTransform;

                    if (record.HasFilterList)
                    {
                        p = SkipFilterList(p);
                    }

                    if (record.HasBlendMode)
                    {
                        record.BlendMode = data[p++];
                    }

                    button.Records.Add(record);
                }

                if (actionOffset != 0)
                {
                    p = actionOffsetField + actionOffset;

                    while (p + 4 <= tagEnd)
                    {
                        int actionRecordStart = p;
                        ushort conditionActionSize = ReadUInt16LEAt(p);
                        p += 2;

                        ushort conditions = ReadUInt16LEAt(p);
                        p += 2;

                        int nextAction = conditionActionSize == 0
                            ? tagEnd
                            : actionRecordStart + conditionActionSize;

                        nextAction = Mathf.Clamp(nextAction, p, tagEnd);

                        byte[] actionBytes = new byte[nextAction - p];
                        Array.Copy(data, p, actionBytes, 0, actionBytes.Length);

                        button.Actions.Add(new SwfButtonCondAction
                        {
                            Conditions = conditions,
                            ActionBytes = actionBytes
                        });

                        p = nextAction;

                        if (conditionActionSize == 0)
                            break;
                    }
                }

                Buttons2.Add(button);

                if (VerboseLogging)
                {
                    Debug.Log(button.ToString());
                }
            }
            catch (Exception e)
            {
                if (DiagnosticsLogging)
                    Debug.LogWarning("Failed to parse DefineButton2 at " + tag.DataStart + ": " + e.Message);
            }
        }

        // SoundStreamHead (18) and SoundStreamHead2 (45) share a layout; the second
        // form simply allows the stream format to differ from the playback format.
        private void ParseSoundStreamHead(SwfTag tag)
        {
            try
            {
                SwfSoundStreamHead head = ParseSoundStreamHeadFromTag(tag);

                if (head == null)
                    return;

                // A head with no samples per frame declares "no stream follows", which
                // authoring tools emit routinely; keep the first real one.
                if (head.SamplesPerFrame == 0 && SoundStreamHead != null)
                    return;

                SoundStreamHead = head;

                if (!Audio.SwfSoundFormats.IsSupported(head.StreamFormat))
                    UnsupportedSoundFormats.Add(head.StreamFormat);

                if (VerboseLogging)
                    Debug.Log(head.ToString());
            }
            catch (Exception e)
            {
                if (DiagnosticsLogging)
                    Debug.LogWarning("Failed to parse SoundStreamHead: " + e.Message);
            }
        }

        private void ParseSoundStreamBlock(SwfTag tag)
        {
            try
            {
                SwfSoundStreamBlock block = ParseSoundStreamBlockFromTag(
                    tag, SoundStreamHead, RootFrames.Count);

                if (block != null)
                    SoundStreamBlocks.Add(block);
            }
            catch (Exception e)
            {
                if (DiagnosticsLogging)
                    Debug.LogWarning("Failed to parse SoundStreamBlock: " + e.Message);
            }
        }

        // SOUNDINFO follows the sound id in a StartSound tag and controls how that
        // sound is played back.
        public SwfSoundInfo ParseSoundInfo(byte[] payload, int offset, out int afterOffset)
        {
            afterOffset = offset;

            if (payload == null || offset >= payload.Length)
                return null;

            byte flags = payload[offset++];
            SwfSoundInfo info = new SwfSoundInfo
            {
                SyncStop = (flags & 0x20) != 0,
                SyncNoMultiple = (flags & 0x10) != 0,
                HasEnvelope = (flags & 0x08) != 0,
                HasLoops = (flags & 0x04) != 0,
                HasOutPoint = (flags & 0x02) != 0,
                HasInPoint = (flags & 0x01) != 0
            };

            if (info.HasInPoint && offset + 4 <= payload.Length)
            {
                info.InPoint = BitConverter.ToUInt32(payload, offset);
                offset += 4;
            }

            if (info.HasOutPoint && offset + 4 <= payload.Length)
            {
                info.OutPoint = BitConverter.ToUInt32(payload, offset);
                offset += 4;
            }

            if (info.HasLoops && offset + 2 <= payload.Length)
            {
                info.LoopCount = (ushort)(payload[offset] | (payload[offset + 1] << 8));
                offset += 2;
            }

            if (info.HasEnvelope && offset < payload.Length)
            {
                int points = payload[offset++];
                info.Envelope = new List<SwfSoundEnvelopePoint>(points);

                for (int i = 0; i < points && offset + 8 <= payload.Length; i++)
                {
                    info.Envelope.Add(new SwfSoundEnvelopePoint
                    {
                        Position44 = BitConverter.ToUInt32(payload, offset),
                        LeftLevel = (ushort)(payload[offset + 4] | (payload[offset + 5] << 8)),
                        RightLevel = (ushort)(payload[offset + 6] | (payload[offset + 7] << 8))
                    });

                    offset += 8;
                }
            }

            afterOffset = offset;
            return info;
        }

        private void ParseDefineSound(SwfTag tag)
        {
            try
            {
                int p = tag.DataStart;
                int tagEnd = tag.DataStart + tag.Length;

                ushort soundId = ReadUInt16LEAt(p);
                p += 2;

                byte soundFlags = data[p++];
                byte soundFormat = (byte)(soundFlags >> 4);
                int rateCode = (soundFlags >> 2) & 0x03;

                DefineSoundTag sound = new DefineSoundTag
                {
                    SoundId = soundId,
                    SoundFormat = soundFormat,
                    SampleRate = GetSoundSampleRate(rateCode),
                    Is16Bit = (soundFlags & 0x02) != 0,
                    IsStereo = (soundFlags & 0x01) != 0,
                    SampleCount = ReadUInt32LEAt(p)
                };

                p += 4;

                if (sound.IsMp3)
                {
                    sound.Mp3SeekSamples = (short)ReadUInt16LEAt(p);
                    p += 2;
                }

                int soundDataLength = Mathf.Max(0, tagEnd - p);
                sound.SoundData = new byte[soundDataLength];
                Array.Copy(data, p, sound.SoundData, 0, soundDataLength);

                Sounds.Add(sound);

                if (!Audio.SwfSoundFormats.IsSupported(sound.SoundFormat))
                    UnsupportedSoundFormats.Add(sound.SoundFormat);

                if (VerboseLogging)
                    Debug.Log(sound.ToString());
            }
            catch (Exception e)
            {
                if (DiagnosticsLogging)
                    Debug.LogWarning("Failed to parse DefineSound at " + tag.DataStart + ": " + e.Message);
            }
        }

        private void ParseExportAssets(SwfTag tag)
        {
            try
            {
                int p = tag.DataStart;
                int tagEnd = tag.DataStart + tag.Length;
                ushort count = ReadUInt16LEAt(p);
                p += 2;

                for (int i = 0; i < count && p < tagEnd; i++)
                {
                    ushort characterId = ReadUInt16LEAt(p);
                    p += 2;

                    int nameStart = p;

                    while (p < tagEnd && data[p] != 0)
                        p++;

                    string exportName = System.Text.Encoding.UTF8.GetString(
                        data,
                        nameStart,
                        p - nameStart
                    );

                    if (p < tagEnd)
                        p++;

                    if (!string.IsNullOrEmpty(exportName))
                    {
                        ExportedAssets[exportName] = characterId;
                    }
                }
            }
            catch (Exception e)
            {
                if (DiagnosticsLogging)
                    Debug.LogWarning("Failed to parse ExportAssets at " + tag.DataStart + ": " + e.Message);
            }
        }

        private int GetSoundSampleRate(int rateCode)
        {
            switch (rateCode)
            {
                case 0:
                    return 5512;
                case 1:
                    return 11025;
                case 2:
                    return 22050;
                default:
                    return 44100;
            }
        }

        private int SkipFilterList(int offset)
        {
            int p = offset;
            int filterCount = data[p++];

            for (int i = 0; i < filterCount; i++)
            {
                byte filterId = data[p++];

                switch (filterId)
                {
                    case 0: // DropShadowFilter
                        p += 23;
                        break;

                    case 1: // BlurFilter
                        p += 9;
                        break;

                    case 2: // GlowFilter
                        p += 15;
                        break;

                    case 3: // BevelFilter
                        p += 27;
                        break;

                    case 4: // GradientGlowFilter
                    case 7: // GradientBevelFilter
                    {
                        int colorCount = data[p++];
                        p += colorCount * 4;
                        p += colorCount;
                        p += 19;
                        break;
                    }

                    case 5: // ConvolutionFilter
                    {
                        int matrixX = data[p++];
                        int matrixY = data[p++];
                        p += 8;
                        p += matrixX * matrixY * 4;
                        p += 5;
                        break;
                    }

                    case 6: // ColorMatrixFilter
                        p += 80;
                        break;

                    default:
                        throw new Exception("Unknown button filter id: " + filterId);
                }
            }

            return p;
        }

        private void ParseDefineShape(SwfTag tag)
        {
            try
            {
                int p = tag.DataStart;

                ushort characterId = ReadUInt16LEAt(p);
                p += 2;

                SwfRect bounds = ReadRectAt(p, out int afterBounds);
                p = afterBounds;

                int shapeVersion = tag.Code == 2 ? 1
                    : tag.Code == 22 ? 2
                    : tag.Code == 32 ? 3
                    : tag.Code == 83 ? 4
                    : 1;

                if (shapeVersion == 4)
                {
                    // DefineShape4 additionally carries an EdgeBounds RECT and a
                    // one-byte flag field (reserved / non-scaling / scaling / winding rule)
                    // before the fill/line style arrays begin.
                    ReadRectAt(p, out int afterEdgeBounds);
                    p = afterEdgeBounds;
                    p += 1;
                }

                DefineShapeTag shape = new DefineShapeTag
                {
                    CharacterId = characterId,
                    ShapeBounds = bounds
                };
                Shapes.Add(shape);

                if (LazyShapeParsing)
                {
                    deferredShapes[characterId] = new DeferredShapeInfo
                    {
                        Shape = shape,
                        StylesOffset = p,
                        ShapeVersion = shapeVersion
                    };
                }
                else
                {
                    ParseDeferredShape(new DeferredShapeInfo
                    {
                        Shape = shape,
                        StylesOffset = p,
                        ShapeVersion = shapeVersion
                    });
                }
            }
            catch (System.Exception e)
            {
                if (DiagnosticsLogging)
                    Debug.LogWarning("Failed to parse DefineShape at " + tag.DataStart + ": " + e.Message);
            }
        }

        // Morph shapes contain two complete outlines and paired styles. Until the
        // renderer can interpolate every edge at PlaceObject.Ratio, retain the
        // start outline as a normal shape instead of dropping the character
        // completely. This is deterministic for ratio zero and gives every SWF a
        // useful fallback without relying on movie-specific character ids.
        private void ParseDefineMorphShapeFallback(SwfTag tag)
        {
            try
            {
                int end = tag.DataStart + tag.Length;
                int p = tag.DataStart;
                if (p + 2 > end)
                    throw new Exception("truncated character id");

                ushort characterId = ReadUInt16LEAt(p);
                p += 2;

                SwfRect startBounds = ReadRectAt(p, out p);
                ReadRectAt(p, out p); // EndBounds

                bool morphShape2 = tag.Code == 84;
                if (morphShape2)
                {
                    ReadRectAt(p, out p); // StartEdgeBounds
                    ReadRectAt(p, out p); // EndEdgeBounds
                    if (p >= end)
                        throw new Exception("truncated MorphShape2 flags");
                    p++; // reserved + UsesNonScalingStrokes + UsesScalingStrokes
                }

                if (p + 4 > end)
                    throw new Exception("truncated EndEdgesOffset");
                p += 4; // EndEdgesOffset; parsing advances through start data itself.

                SwfShapeData shapeData = new SwfShapeData
                {
                    CharacterId = characterId,
                    // All morph colours are RGBA. MorphShape2 additionally uses
                    // LINESTYLE2, matching the regular shape v4 representation.
                    ShapeVersion = morphShape2 ? 4 : 3
                };

                int fillCount = ReadExtendedStyleCount(ref p, end);
                shapeData.FillStyleCount = fillCount;
                for (int i = 0; i < fillCount; i++)
                    p = ReadMorphFillStyleStart(p, end, shapeData);

                int lineCount = ReadExtendedStyleCount(ref p, end);
                shapeData.LineStyleCount = lineCount;
                for (int i = 0; i < lineCount; i++)
                {
                    p = morphShape2
                        ? ReadMorphLineStyle2Start(p, end, shapeData)
                        : ReadMorphLineStyleStart(p, end, shapeData);
                }

                if (p >= end)
                    throw new Exception("missing StartEdges");

                SwfBitReader edgeHeader = new SwfBitReader(data, p);
                shapeData.NumFillBits = (int)edgeHeader.ReadUBits(4);
                shapeData.NumLineBits = (int)edgeHeader.ReadUBits(4);
                edgeHeader.AlignToByte();

                ParseShapeRecords(shapeData, edgeHeader.BytePosition);
                BuildEdgesFromPaths(shapeData);
                BuildFillEdgeGroups(shapeData);
                BuildFillContours(shapeData);

                Shapes.Add(new DefineShapeTag
                {
                    CharacterId = characterId,
                    ShapeBounds = startBounds,
                    ShapeData = shapeData
                });
            }
            catch (Exception e)
            {
                if (DiagnosticsLogging)
                {
                    Debug.LogWarning(
                        "Failed to parse " + SwfTagNames.GetName(tag.Code) +
                        " start shape at " + tag.DataStart + ": " + e.Message);
                }
            }
        }

        private int ReadExtendedStyleCount(ref int p, int end)
        {
            if (p >= end)
                throw new Exception("truncated style count");

            int count = data[p++];
            if (count == 0xFF)
            {
                if (p + 2 > end)
                    throw new Exception("truncated extended style count");
                count = ReadUInt16LEAt(p);
                p += 2;
            }

            return count;
        }

        private int ReadMorphFillStyleStart(int p, int end, SwfShapeData shapeData)
        {
            if (p >= end)
                throw new Exception("truncated morph fill style");

            SwfFillStyle fill = new SwfFillStyle { FillType = data[p++] };
            switch (fill.FillType)
            {
                case 0x00:
                    EnsureTagBytes(p, 8, end, "morph solid colours");
                    fill.R = data[p];
                    fill.G = data[p + 1];
                    fill.B = data[p + 2];
                    fill.A = data[p + 3];
                    p += 8; // start RGBA + end RGBA
                    break;

                case 0x10:
                case 0x12:
                case 0x13:
                    fill.GradientMatrix = ReadMatrixAt(p, out p);
                    ReadMatrixAt(p, out p); // EndGradientMatrix
                    EnsureTagBytes(p, 1, end, "morph gradient count");
                    // SWF 8+ packs SpreadMode and InterpolationMode into the high
                    // nibble, just like GRADIENT. Only the low nibble is a count.
                    int gradientCount = data[p++] & 0x0F;
                    fill.GradientStops = new List<SwfGradientStop>(gradientCount);
                    for (int i = 0; i < gradientCount; i++)
                    {
                        EnsureTagBytes(p, 10, end, "morph gradient record");
                        byte startRatio = data[p++];
                        byte r = data[p++];
                        byte g = data[p++];
                        byte b = data[p++];
                        byte a = data[p++];
                        p += 5; // end ratio + end RGBA
                        fill.GradientStops.Add(new SwfGradientStop
                        {
                            Ratio = startRatio,
                            Color = new Color(r / 255f, g / 255f, b / 255f, a / 255f)
                        });
                    }

                    if (fill.FillType == 0x13)
                    {
                        EnsureTagBytes(p, 4, end, "morph focal points");
                        fill.FocalPoint = (short)ReadUInt16LEAt(p) / 256f;
                        p += 4; // start + end FIXED8
                    }
                    break;

                case 0x40:
                case 0x41:
                case 0x42:
                case 0x43:
                    EnsureTagBytes(p, 2, end, "morph bitmap id");
                    fill.BitmapId = ReadUInt16LEAt(p);
                    p += 2;
                    fill.BitmapMatrix = ReadMatrixAt(p, out p);
                    ReadMatrixAt(p, out p); // EndBitmapMatrix
                    fill.BitmapSmoothed = fill.FillType == 0x40 || fill.FillType == 0x41;
                    fill.BitmapClipped = fill.FillType == 0x41 || fill.FillType == 0x43;
                    break;

                default:
                    throw new Exception(
                        "unknown morph fill style 0x" + fill.FillType.ToString("X2"));
            }

            shapeData.FillStyles.Add(fill);
            return p;
        }

        private int ReadMorphLineStyleStart(int p, int end, SwfShapeData shapeData)
        {
            EnsureTagBytes(p, 12, end, "morph line style");
            SwfLineStyle line = new SwfLineStyle
            {
                Width = ReadUInt16LEAt(p) / 20f,
                Color = new Color(
                    data[p + 4] / 255f,
                    data[p + 5] / 255f,
                    data[p + 6] / 255f,
                    data[p + 7] / 255f)
            };
            p += 12; // paired widths + paired RGBA colours
            shapeData.LineStyles.Add(line);
            return p;
        }

        private int ReadMorphLineStyle2Start(int p, int end, SwfShapeData shapeData)
        {
            EnsureTagBytes(p, 6, end, "morph line style 2");
            SwfLineStyle line = new SwfLineStyle
            {
                Width = ReadUInt16LEAt(p) / 20f
            };
            p += 4; // start + end width

            SwfBitReader reader = new SwfBitReader(data, p);
            line.StartCapStyle = (int)reader.ReadUBits(2);
            line.JoinStyle = (int)reader.ReadUBits(2);
            line.HasFillStyle = reader.ReadUBits(1) == 1;
            line.NoHScale = reader.ReadUBits(1) == 1;
            line.NoVScale = reader.ReadUBits(1) == 1;
            line.PixelHinting = reader.ReadUBits(1) == 1;
            reader.ReadUBits(5);
            line.NoClose = reader.ReadUBits(1) == 1;
            line.EndCapStyle = (int)reader.ReadUBits(2);
            reader.AlignToByte();
            p = reader.BytePosition;

            if (line.JoinStyle == 2)
            {
                EnsureTagBytes(p, 2, end, "morph miter limit");
                line.MiterLimitFactor = (short)ReadUInt16LEAt(p) / 256f;
                p += 2;
            }

            if (line.HasFillStyle)
            {
                line.FillStyleIndex = shapeData.FillStyles.Count;
                p = ReadMorphFillStyleStart(p, end, shapeData);
            }
            else
            {
                EnsureTagBytes(p, 8, end, "morph line colours");
                line.Color = new Color(
                    data[p] / 255f,
                    data[p + 1] / 255f,
                    data[p + 2] / 255f,
                    data[p + 3] / 255f);
                p += 8;
            }

            shapeData.LineStyles.Add(line);
            return p;
        }

        private static void EnsureTagBytes(int p, int count, int end, string field)
        {
            if (p < 0 || count < 0 || p > end - count)
                throw new Exception("truncated " + field);
        }

        private SwfMatrix ReadMatrixAt(int offset, out int nextBytePosition)
        {
            SwfBitReader reader = new SwfBitReader(data, offset);

            SwfMatrix matrix = SwfMatrix.Identity;

            bool hasScale = reader.ReadUBits(1) == 1;
            if (hasScale)
            {
                int nScaleBits = (int)reader.ReadUBits(5);
                matrix.ScaleX = reader.ReadSBits(nScaleBits) / 65536f;
                matrix.ScaleY = reader.ReadSBits(nScaleBits) / 65536f;
            }

            bool hasRotate = reader.ReadUBits(1) == 1;
            if (hasRotate)
            {
                int nRotateBits = (int)reader.ReadUBits(5);
                matrix.RotateSkew0 = reader.ReadSBits(nRotateBits) / 65536f;
                matrix.RotateSkew1 = reader.ReadSBits(nRotateBits) / 65536f;
            }

            int nTranslateBits = (int)reader.ReadUBits(5);
            matrix.TranslateX = reader.ReadSBits(nTranslateBits) / 20f;
            matrix.TranslateY = reader.ReadSBits(nTranslateBits) / 20f;

            reader.AlignToByte();

            nextBytePosition = reader.BytePosition;

            return matrix;
        }

        private void ParsePlaceObject2(SwfTag tag)
        {
            try
            {
                PlaceObject2Tag place = ParsePlaceObject2FromTag(tag);

                PlacedObjects.Add(place);

                if (VerboseLogging)
                    Debug.Log(place.ToString());
            }
            catch (System.Exception e)
            {
                if (DiagnosticsLogging)
                    Debug.LogWarning("Failed to parse PlaceObject2 at " + tag.DataStart + ": " + e.Message);
            }
        }

        // Character lookups run once per placed object per rendered frame, so the
        // original linear scans were O(placedObjects * charactersOfThatType) every
        // frame. The dictionaries are built lazily on first lookup (all parsing has
        // finished by the time anything renders) and never change afterwards, so a
        // single index build replaces the repeated scans without touching parsing.
        private Dictionary<ushort, DefineShapeTag> shapeIndex;
        private Dictionary<ushort, DefineSpriteTag> spriteIndex;
        private Dictionary<ushort, DefineBitmapTag> bitmapIndex;
        private Dictionary<ushort, DefineButton2Tag> button2Index;
        private Dictionary<ushort, DefineSoundTag> soundIndex;
        private Dictionary<ushort, DefineTextTag> textIndex;
        private Dictionary<ushort, DefineEditTextTag> editTextIndex;
        private Dictionary<ushort, DefineFont3Tag> font3Index;

        public DefineShapeTag FindShapeById(ushort id)
        {
            if (shapeIndex == null)
            {
                shapeIndex = new Dictionary<ushort, DefineShapeTag>(Shapes.Count);
                for (int i = 0; i < Shapes.Count; i++)
                    shapeIndex[Shapes[i].CharacterId] = Shapes[i];
            }

            if (!shapeIndex.TryGetValue(id, out DefineShapeTag shape))
                return null;

            EnsureShapeParsed(shape);
            return shape;
        }

        public DefineSpriteTag FindSpriteById(ushort id)
        {
            if (spriteIndex == null)
            {
                spriteIndex = new Dictionary<ushort, DefineSpriteTag>(Sprites.Count);
                for (int i = 0; i < Sprites.Count; i++)
                    spriteIndex[Sprites[i].SpriteId] = Sprites[i];
            }

            return spriteIndex.TryGetValue(id, out DefineSpriteTag sprite) ? sprite : null;
        }

        public DefineButton2Tag FindButton2ById(ushort id)
        {
            if (button2Index == null)
            {
                button2Index = new Dictionary<ushort, DefineButton2Tag>(Buttons2.Count);
                for (int i = 0; i < Buttons2.Count; i++)
                    button2Index[Buttons2[i].ButtonId] = Buttons2[i];
            }

            return button2Index.TryGetValue(id, out DefineButton2Tag button) ? button : null;
        }

        public DefineSoundTag FindSoundById(ushort id)
        {
            if (soundIndex == null)
            {
                soundIndex = new Dictionary<ushort, DefineSoundTag>(Sounds.Count);
                for (int i = 0; i < Sounds.Count; i++)
                    soundIndex[Sounds[i].SoundId] = Sounds[i];
            }

            return soundIndex.TryGetValue(id, out DefineSoundTag sound) ? sound : null;
        }

        public DefineSoundTag FindExportedSound(string exportName)
        {
            if (string.IsNullOrEmpty(exportName))
                return null;

            if (!ExportedAssets.TryGetValue(exportName, out ushort characterId))
                return null;

            return FindSoundById(characterId);
        }

        public byte[] CopyTagData(SwfTag tag, int skipBytes = 0)
        {
            if (tag == null)
                return new byte[0];

            int skip = Mathf.Clamp(skipBytes, 0, tag.Length);
            int start = Mathf.Clamp(tag.DataStart + skip, 0, data.Length);
            int length = Mathf.Clamp(tag.Length - skip, 0, data.Length - start);
            byte[] result = new byte[length];
            Array.Copy(data, start, result, 0, length);
            return result;
        }

        public ushort ParseDoInitActionSpriteId(SwfTag tag)
        {
            if (tag == null || tag.Length < 2)
                return 0;

            return ReadUInt16LEAt(tag.DataStart);
        }

        private byte ReadByteAt(int offset)
        {
            return data[offset];
        }

        public PlaceObject2Tag ParsePlaceObject2FromTag(SwfTag tag)
        {
            int p = tag.DataStart;

            byte flags = data[p];
            p++;
            byte extendedFlags = tag.Code == 70 && p < tag.DataStart + tag.Length
                ? data[p++]
                : (byte)0;

            PlaceObject2Tag place = new PlaceObject2Tag
            {
                HasClipActions = (flags & 0x80) != 0,
                HasClipDepth = (flags & 0x40) != 0,
                HasName = (flags & 0x20) != 0,
                HasRatio = (flags & 0x10) != 0,
                HasColorTransform = (flags & 0x08) != 0,
                HasMatrix = (flags & 0x04) != 0,
                HasCharacter = (flags & 0x02) != 0,
                Move = (flags & 0x01) != 0,
                HasMove = (flags & 0x01) != 0,
                HasFilterList = (extendedFlags & 0x01) != 0,
                HasBlendMode = (extendedFlags & 0x02) != 0,
                HasCacheAsBitmap = (extendedFlags & 0x04) != 0,
                HasClassName = (extendedFlags & 0x08) != 0,
                HasVisible = (extendedFlags & 0x20) != 0,
                HasOpaqueBackground = (extendedFlags & 0x40) != 0
            };

            place.Depth = ReadUInt16LEAt(p);
            p += 2;

            if (place.HasClassName ||
                (tag.Code == 70 && (extendedFlags & 0x10) != 0 && place.HasCharacter))
            {
                place.ClassName = ReadNullTerminatedString(ref p, tag.DataStart + tag.Length);
            }

            if (place.HasCharacter)
            {
                place.CharacterId = ReadUInt16LEAt(p);
                p += 2;
            }

            if (place.HasMatrix)
            {
                place.Matrix = ReadMatrixAt(p, out int afterMatrix);
                p = afterMatrix;
            }
            else
            {
                place.Matrix = SwfMatrix.Identity;
            }

            if (place.HasColorTransform)
            {
                place.ColorTransform = ReadColorTransformWithAlpha(p, out int afterCxForm);
                p = afterCxForm;
            }
            else
            {
                place.ColorTransform = SwfColorTransform.Identity;
            }

            if (place.HasRatio)
            {
                place.Ratio = ReadUInt16LEAt(p);
                p += 2;
            }

            if (place.HasName)
            {
                int nameStart = p;

                while (p < tag.DataStart + tag.Length && data[p] != 0)
                {
                    p++;
                }

                int nameLength = p - nameStart;

                if (nameLength > 0)
                {
                    place.Name = System.Text.Encoding.UTF8.GetString(data, nameStart, nameLength);
                }
                else
                {
                    place.Name = "";
                }

                // Skip null terminator.
                if (p < tag.DataStart + tag.Length)
                {
                    p++;
                }
            }

            if (place.HasClipDepth)
            {
                place.ClipDepth = ReadUInt16LEAt(p);
                p += 2;
            }

            if (place.HasFilterList)
                SkipFilterList(ref p, tag.DataStart + tag.Length);

            if (place.HasBlendMode && p < tag.DataStart + tag.Length)
                place.BlendMode = data[p++];

            if (place.HasCacheAsBitmap && p < tag.DataStart + tag.Length)
                place.BitmapCache = data[p++];

            if (place.HasVisible && p < tag.DataStart + tag.Length)
                place.Visible = data[p++];

            if (place.HasOpaqueBackground && p + 4 <= tag.DataStart + tag.Length)
                p += 4;

            if (place.HasClipActions)
                ReadClipActions(place, ref p, tag.DataStart + tag.Length);

            if (VerboseLogging && (place.HasClipDepth || place.HasRatio))
            {
                Debug.Log(
                    "PLACE SPECIAL " +
                    "Depth=" + place.Depth +
                    " Char=" + place.CharacterId +
                    " HasClipDepth=" + place.HasClipDepth +
                    " ClipDepth=" + place.ClipDepth +
                    " HasRatio=" + place.HasRatio +
                    " Ratio=" + place.Ratio +
                    " HasCX=" + place.HasColorTransform
                );
            }

            return place;
        }

        private string ReadNullTerminatedString(ref int p, int end)
        {
            int start = p;

            while (p < end && data[p] != 0)
                p++;

            string value = System.Text.Encoding.UTF8.GetString(data, start, p - start);

            if (p < end)
                p++;

            return value;
        }

        private void SkipFilterList(ref int p, int end)
        {
            if (p >= end)
                return;

            int count = data[p++];

            for (int i = 0; i < count && p < end; i++)
            {
                byte filterId = data[p++];
                int fixedLength;

                switch (filterId)
                {
                    case 0: fixedLength = 23; break; // DropShadow
                    case 1: fixedLength = 9; break;  // Blur
                    case 2: fixedLength = 15; break; // Glow
                    case 3: fixedLength = 27; break; // Bevel
                    case 4:
                    case 7:
                    {
                        int colors = p < end ? data[p++] : 0;
                        fixedLength = colors * 5 + 19;
                        break;
                    }
                    case 5:
                    {
                        int matrixX = p < end ? data[p++] : 0;
                        int matrixY = p < end ? data[p++] : 0;
                        fixedLength = 13 + matrixX * matrixY * 4;
                        break;
                    }
                    case 6: fixedLength = 80; break; // ColorMatrix
                    default:
                        p = end;
                        return;
                }

                p = Math.Min(end, p + fixedLength);
            }
        }

        private void ReadClipActions(PlaceObject2Tag place, ref int p, int end)
        {
            if (place == null || p + 2 > end)
                return;

            p += 2; // Reserved
            int eventFlagSize = Header != null && Header.Version >= 6 ? 4 : 2;

            if (p + eventFlagSize > end)
                return;

            p += eventFlagSize; // AllEventFlags

            while (p + eventFlagSize <= end)
            {
                uint eventFlags = eventFlagSize == 4
                    ? ReadUInt32LEAt(p)
                    : ReadUInt16LEAt(p);
                p += eventFlagSize;

                if (eventFlags == 0)
                    break;

                if (p + 4 > end)
                    break;

                int actionRecordSize = (int)ReadUInt32LEAt(p);
                p += 4;
                int recordEnd = Math.Min(end, p + Math.Max(0, actionRecordSize));
                byte keyCode = 0;

                if ((eventFlags & 0x00020000u) != 0 && p < recordEnd)
                    keyCode = data[p++];

                int actionLength = Math.Max(0, recordEnd - p);
                byte[] actions = new byte[actionLength];
                Array.Copy(data, p, actions, 0, actionLength);
                place.ClipActions.Add(new SwfClipActionRecord
                {
                    EventFlags = eventFlags,
                    KeyCode = keyCode,
                    ActionBytes = actions
                });
                p = recordEnd;
            }
        }

        private SwfColorTransform ReadColorTransformWithAlpha(int offset, out int afterOffset)
        {
            SwfBitReader reader = new SwfBitReader(data, offset);

            SwfColorTransform cx = new SwfColorTransform();

            bool hasAddTerms = reader.ReadUBits(1) == 1;
            bool hasMultTerms = reader.ReadUBits(1) == 1;

            int nbits = (int)reader.ReadUBits(4);

            cx.HasAddTerms = hasAddTerms;
            cx.HasMultTerms = hasMultTerms;

            if (hasMultTerms)
            {
                cx.RedMult = reader.ReadSBits(nbits);
                cx.GreenMult = reader.ReadSBits(nbits);
                cx.BlueMult = reader.ReadSBits(nbits);
                cx.AlphaMult = reader.ReadSBits(nbits);
            }

            if (hasAddTerms)
            {
                cx.RedAdd = reader.ReadSBits(nbits);
                cx.GreenAdd = reader.ReadSBits(nbits);
                cx.BlueAdd = reader.ReadSBits(nbits);
                cx.AlphaAdd = reader.ReadSBits(nbits);
            }

            reader.AlignToByte();
            afterOffset = reader.BytePosition;

            return cx;
        }

        private SwfShapeData ParseShapeStylesOnly(ushort characterId, int offset, int shapeVersion, out int afterStylesPosition)
        {
            int p = offset;

            SwfShapeData shapeData = new SwfShapeData
            {
                CharacterId = characterId,
                ShapeVersion = shapeVersion
            };

            int fillStyleCount = ReadByteAt(p);
            p++;

            if (fillStyleCount == 0xFF)
            {
                fillStyleCount = ReadUInt16LEAt(p);
                p += 2;
            }

            shapeData.FillStyleCount = fillStyleCount;

            for (int i = 0; i < fillStyleCount; i++)
            {
                p = ReadFillStyle(p, shapeData, shapeVersion);
            }

            int lineStyleCount = ReadByteAt(p);
            p++;

            if (lineStyleCount == 0xFF)
            {
                lineStyleCount = ReadUInt16LEAt(p);
                p += 2;
            }

            shapeData.LineStyleCount = lineStyleCount;

            for (int i = 0; i < lineStyleCount; i++)
            {
                p = ReadLineStyle(p, shapeData, shapeVersion);
            }

            SwfBitReader reader = new SwfBitReader(data, p);

            shapeData.NumFillBits = (int)reader.ReadUBits(4);
            shapeData.NumLineBits = (int)reader.ReadUBits(4);

            reader.AlignToByte();

            afterStylesPosition = reader.BytePosition;

            return shapeData;
        }

        private int ReadFillStyle(int p, SwfShapeData shapeData, int shapeVersion)
        {
            SwfFillStyle fill = new SwfFillStyle();

            fill.FillType = data[p];
            p++;

            // DefineShape/DefineShape2 (v1/v2) use RGB solid/gradient colors.
            // DefineShape3/DefineShape4 (v3/v4) use RGBA throughout.
            bool useRgba = shapeVersion >= 3;

            if (fill.FillType == 0x00)
            {
                fill.R = data[p];
                fill.G = data[p + 1];
                fill.B = data[p + 2];
                fill.A = useRgba ? data[p + 3] : (byte)255;

                p += useRgba ? 4 : 3;

                shapeData.FillStyles.Add(fill);
            }
            // Gradient fill (0x10 linear, 0x12 radial, 0x13 focal radial - v4 only)
            else if (fill.FillType == 0x10 || fill.FillType == 0x12 || fill.FillType == 0x13)
            {
                fill.GradientMatrix = ReadMatrixAt(p, out p);

                byte gradientInfo = data[p];
                p++;

                int numGradients = gradientInfo & 0x0F;
                fill.GradientStops = new List<SwfGradientStop>(numGradients);

                for (int i = 0; i < numGradients; i++)
                {
                    byte ratio = data[p];
                    p++;

                    byte r = data[p];
                    byte g = data[p + 1];
                    byte b = data[p + 2];
                    byte a = useRgba ? data[p + 3] : (byte)255;
                    p += useRgba ? 4 : 3;

                    fill.GradientStops.Add(new SwfGradientStop
                    {
                        Ratio = ratio,
                        Color = new Color(r / 255f, g / 255f, b / 255f, a / 255f)
                    });
                }

                if (fill.FillType == 0x13)
                {
                    fill.FocalPoint = (short)ReadUInt16LEAt(p) / 256f;
                    p += 2;
                }

                shapeData.FillStyles.Add(fill);
            }
            // Bitmap fill
            else if (
                fill.FillType == 0x40 ||
                fill.FillType == 0x41 ||
                fill.FillType == 0x42 ||
                fill.FillType == 0x43
            )
            {
                fill.BitmapId = ReadUInt16LEAt(p);
                p += 2;

                fill.BitmapMatrix = ReadMatrixAt(p, out p);
                fill.BitmapSmoothed = fill.FillType == 0x40 || fill.FillType == 0x41;
                fill.BitmapClipped = fill.FillType == 0x41 || fill.FillType == 0x43;

                shapeData.FillStyles.Add(fill);
            }
            else
            {
                if (DiagnosticsLogging)
                    UnityEngine.Debug.LogWarning("Unknown FillStyleType: 0x" + fill.FillType.ToString("X2"));
                shapeData.FillStyles.Add(fill);
            }

            return p;
        }

        private int ReadLineStyle(int p, SwfShapeData shapeData, int shapeVersion)
        {
            SwfLineStyle line = new SwfLineStyle();

            line.Width = ReadUInt16LEAt(p) / 20f;
            p += 2;

            if (shapeVersion < 4)
            {
                // LINESTYLE: RGB for v1/v2, RGBA for v3.
                bool useRgba = shapeVersion >= 3;

                byte r = data[p];
                byte g = data[p + 1];
                byte b = data[p + 2];
                byte a = useRgba ? data[p + 3] : (byte)255;
                p += useRgba ? 4 : 3;

                line.Color = new Color(r / 255f, g / 255f, b / 255f, a / 255f);
            }
            else
            {
                // LINESTYLE2 (DefineShape4): caps/joins/scaling flags, optional
                // miter limit, and either a solid RGBA color or a fill style.
                SwfBitReader reader = new SwfBitReader(data, p);

                line.StartCapStyle = (int)reader.ReadUBits(2);
                line.JoinStyle = (int)reader.ReadUBits(2);
                line.HasFillStyle = reader.ReadUBits(1) == 1;
                line.NoHScale = reader.ReadUBits(1) == 1;
                line.NoVScale = reader.ReadUBits(1) == 1;
                line.PixelHinting = reader.ReadUBits(1) == 1;
                reader.ReadUBits(5); // reserved
                line.NoClose = reader.ReadUBits(1) == 1;
                line.EndCapStyle = (int)reader.ReadUBits(2);

                reader.AlignToByte();
                p = reader.BytePosition;

                if (line.JoinStyle == 2)
                {
                    line.MiterLimitFactor = (short)ReadUInt16LEAt(p) / 256f;
                    p += 2;
                }

                if (line.HasFillStyle)
                {
                    line.FillStyleIndex = shapeData.FillStyles.Count;
                    p = ReadFillStyle(p, shapeData, shapeVersion);
                }
                else
                {
                    byte r = data[p];
                    byte g = data[p + 1];
                    byte b = data[p + 2];
                    byte a = data[p + 3];
                    p += 4;

                    line.Color = new Color(r / 255f, g / 255f, b / 255f, a / 255f);
                }
            }

            shapeData.LineStyles.Add(line);
            return p;
        }

        private void ParseShapeRecords(SwfShapeData shapeData, int offset)
        {
            SwfBitReader reader = new SwfBitReader(data, offset);

            int numFillBits = shapeData.NumFillBits;
            int numLineBits = shapeData.NumLineBits;

            int currentX = 0;
            int currentY = 0;

            int currentFillStyle0 = 0;
            int currentFillStyle1 = 0;
            int currentLineStyle = 0;

            SwfShapePath currentPath = null;

            int fillIndexOffset = 0;
            int lineIndexOffset = 0;

            int safety = 0;

            while (reader.BytePosition < data.Length && safety < 100000)
            {
                safety++;

                bool typeFlag = reader.ReadUBits(1) == 1;

                if (!typeFlag)
                {
                    uint flags = reader.ReadUBits(5);

                    // EndShapeRecord
                    if (flags == 0)
                    {
                        break;
                    }

                    bool stateNewStyles = (flags & 0x10) != 0;
                    bool stateLineStyle = (flags & 0x08) != 0;
                    bool stateFillStyle1 = (flags & 0x04) != 0;
                    bool stateFillStyle0 = (flags & 0x02) != 0;
                    bool stateMoveTo = (flags & 0x01) != 0;

                    int newX = currentX;
                    int newY = currentY;

                    if (stateMoveTo)
                    {
                        int moveBits = (int)reader.ReadUBits(5);

                        newX = reader.ReadSBits(moveBits);
                        newY = reader.ReadSBits(moveBits);
                    }

                    if (stateFillStyle0)
                    {
                        int localIndex = (int)reader.ReadUBits(numFillBits);
                        currentFillStyle0 = localIndex == 0 ? 0 : localIndex + fillIndexOffset;
                    }

                    if (stateFillStyle1)
                    {
                        int localIndex = (int)reader.ReadUBits(numFillBits);
                        currentFillStyle1 = localIndex == 0 ? 0 : localIndex + fillIndexOffset;
                    }

                    if (stateLineStyle)
                    {
                        int localIndex = (int)reader.ReadUBits(numLineBits);
                        currentLineStyle = localIndex == 0 ? 0 : localIndex + lineIndexOffset;
                    }

                    bool styleChanged =
                        stateFillStyle0 ||
                        stateFillStyle1 ||
                        stateLineStyle;

                    if (styleChanged && !stateMoveTo)
                    {
                        // Start a new path at the current position when the fill/line style changes.
                        // SWF can change styles without moving to a new position.
                        currentPath = new SwfShapePath
                        {
                            FillStyle0 = currentFillStyle0,
                            FillStyle1 = currentFillStyle1,
                            LineStyle = currentLineStyle
                        };

                        currentPath.Points.Add(new Vector2(currentX / 20f, currentY / 20f));
                        shapeData.Paths.Add(currentPath);
                    }

                    if (stateNewStyles)
                    {
                        // A fresh FillStyleArray/LineStyleArray follows, byte-aligned.
                        // Style indices read after this point are local to the new
                        // arrays, so remember the offset needed to map them into the
                        // single combined FillStyles/LineStyles lists on shapeData.
                        reader.AlignToByte();
                        int bytePos = reader.BytePosition;

                        int newFillCount = ReadByteAt(bytePos);
                        bytePos++;

                        if (newFillCount == 0xFF)
                        {
                            newFillCount = ReadUInt16LEAt(bytePos);
                            bytePos += 2;
                        }

                        int fillBaseIndex = shapeData.FillStyles.Count;

                        for (int i = 0; i < newFillCount; i++)
                            bytePos = ReadFillStyle(bytePos, shapeData, shapeData.ShapeVersion);

                        int newLineCount = ReadByteAt(bytePos);
                        bytePos++;

                        if (newLineCount == 0xFF)
                        {
                            newLineCount = ReadUInt16LEAt(bytePos);
                            bytePos += 2;
                        }

                        int lineBaseIndex = shapeData.LineStyles.Count;

                        for (int i = 0; i < newLineCount; i++)
                            bytePos = ReadLineStyle(bytePos, shapeData, shapeData.ShapeVersion);

                        reader = new SwfBitReader(data, bytePos);
                        numFillBits = (int)reader.ReadUBits(4);
                        numLineBits = (int)reader.ReadUBits(4);

                        fillIndexOffset = fillBaseIndex;
                        lineIndexOffset = lineBaseIndex;
                        currentFillStyle0 = 0;
                        currentFillStyle1 = 0;
                        currentLineStyle = 0;

                        // The next edge belongs to the newly installed style arrays.
                        // Keeping the old path object here assigned every later edge
                        // to the pre-NewStyles fill until another MoveTo/style record,
                        // which mixed unrelated outlines into one fill graph.
                        currentPath = null;
                    }

                    if (stateMoveTo)
                    {
                        currentX = newX;
                        currentY = newY;

                        currentPath = new SwfShapePath
                        {
                            FillStyle0 = currentFillStyle0,
                            FillStyle1 = currentFillStyle1,
                            LineStyle = currentLineStyle
                        };

                        currentPath.Points.Add(new Vector2(currentX / 20f, currentY / 20f));
                        shapeData.Paths.Add(currentPath);
                    }

                    continue;
                }
                else
                {
                    bool straightFlag = reader.ReadUBits(1) == 1;
                    int numBits = (int)reader.ReadUBits(4) + 2;

                    if (straightFlag)
                    {
                        bool generalLineFlag = reader.ReadUBits(1) == 1;

                        int dx = 0;
                        int dy = 0;

                        if (generalLineFlag)
                        {
                            dx = reader.ReadSBits(numBits);
                            dy = reader.ReadSBits(numBits);
                        }
                        else
                        {
                            bool verticalLineFlag = reader.ReadUBits(1) == 1;

                            if (verticalLineFlag)
                            {
                                dy = reader.ReadSBits(numBits);
                            }
                            else
                            {
                                dx = reader.ReadSBits(numBits);
                            }
                        }

                        currentPath = EnsureCurrentPath(
                            shapeData,
                            currentPath,
                            currentX,
                            currentY,
                            currentFillStyle0,
                            currentFillStyle1,
                            currentLineStyle
                        );

                        int startX = currentX;
                        int startY = currentY;

                        int endX = currentX + dx;
                        int endY = currentY + dy;

                        // The edge list is rebuilt from these path points by
                        // BuildEdgesFromPaths, so appending the point is the only
                        // thing needed here. Emitting an edge as well allocated every
                        // edge in the movie twice, and the first copy was then thrown
                        // away by Edges.Clear().
                        currentX = endX;
                        currentY = endY;

                        currentPath.Points.Add(new Vector2(currentX / 20f, currentY / 20f));
                    }
                    else
                    {
                        currentPath = EnsureCurrentPath(
                            shapeData,
                            currentPath,
                            currentX,
                            currentY,
                            currentFillStyle0,
                            currentFillStyle1,
                            currentLineStyle
                        );

                        // CurvedEdgeRecord
                        currentPath = EnsureCurrentPath(
                            shapeData,
                            currentPath,
                            currentX,
                            currentY,
                            currentFillStyle0,
                            currentFillStyle1,
                            currentLineStyle
                        );

                        int startX = currentX;
                        int startY = currentY;

                        int controlDeltaX = reader.ReadSBits(numBits);
                        int controlDeltaY = reader.ReadSBits(numBits);
                        int anchorDeltaX = reader.ReadSBits(numBits);
                        int anchorDeltaY = reader.ReadSBits(numBits);

                        int controlX = currentX + controlDeltaX;
                        int controlY = currentY + controlDeltaY;

                        int anchorX = controlX + anchorDeltaX;
                        int anchorY = controlY + anchorDeltaY;

                        // Quadratic Bezier sampling. The segment count comes from the
                        // active quality level: this is where curve fidelity is fixed
                        // for the life of the loaded movie, because the flattened
                        // points are what every later stage consumes.
                        int steps = curveSubdivisionSteps;

                        Vector2 previousPoint = new Vector2(startX / 20f, startY / 20f);

                        for (int s = 1; s <= steps; s++)
                        {
                            float t = s / (float)steps;
                            float inv = 1f - t;

                            float x =
                                inv * inv * startX +
                                2f * inv * t * controlX +
                                t * t * anchorX;

                            float y =
                                inv * inv * startY +
                                2f * inv * t * controlY +
                                t * t * anchorY;

                            Vector2 sampledPoint = new Vector2(x / 20f, y / 20f);

                            currentPath.Points.Add(sampledPoint);
                            previousPoint = sampledPoint;
                        }

                        currentX = anchorX;
                        currentY = anchorY;
                    }
                }
            }

            if (VerboseLogging)
            {
                Debug.Log("Parsed ShapeRecords for " + shapeData.CharacterId + " Paths=" + shapeData.Paths.Count);
            }
        }

        private SwfShapePath EnsureCurrentPath(
            SwfShapeData shapeData,
            SwfShapePath currentPath,
            int currentX,
            int currentY,
            int currentFillStyle0,
            int currentFillStyle1,
            int currentLineStyle
        )
        {
            if (currentPath != null)
                return currentPath;

            currentPath = new SwfShapePath
            {
                FillStyle0 = currentFillStyle0,
                FillStyle1 = currentFillStyle1,
                LineStyle = currentLineStyle
            };

            currentPath.Points.Add(new UnityEngine.Vector2(currentX / 20f, currentY / 20f));
            shapeData.Paths.Add(currentPath);

            return currentPath;
        }

        private void BuildFillEdgeGroups(SwfShapeData shapeData)
        {
            if (shapeData == null || shapeData.Edges == null)
                return;

            shapeData.FillEdgeGroups.Clear();
            fillGroupIndex.Clear();

            for (int i = 0; i < shapeData.Edges.Count; i++)
            {
                SwfShapeEdge edge = shapeData.Edges[i];

                // For now we use FillStyle1 as the primary fill side.
                if (edge.FillStyle1 > 0)
                {
                    AddEdgeToFillGroup(shapeData, edge.FillStyle1, edge);
                }

                // Some SWFs use FillStyle0 for the opposite side.
                // We store it too, but reversed, because FillStyle0 represents the other side of the edge.
                if (edge.FillStyle0 > 0)
                {
                    // Reversing the direction swaps which side each fill is on, so
                    // the two style fields swap with it.
                    SwfShapeEdge reversed = new SwfShapeEdge
                    {
                        Start = edge.End,
                        End = edge.Start,
                        FillStyle0 = edge.FillStyle1,
                        FillStyle1 = edge.FillStyle0,
                        LineStyle = edge.LineStyle
                    };

                    AddEdgeToFillGroup(shapeData, edge.FillStyle0, reversed);
                }
            }

            if (VerboseLogging)
            {
                for (int i = 0; i < shapeData.FillEdgeGroups.Count; i++)
                {
                    Debug.Log(shapeData.FillEdgeGroups[i].ToString());
                }
            }
        }

        // Scratch index reused across shapes so grouping does not allocate a
        // dictionary per shape.
        private readonly Dictionary<int, SwfFillEdgeGroup> fillGroupIndex =
            new Dictionary<int, SwfFillEdgeGroup>();

        private void AddEdgeToFillGroup(
            SwfShapeData shapeData,
            int fillStyleIndex,
            SwfShapeEdge edge
        )
        {
            if (!fillGroupIndex.TryGetValue(fillStyleIndex, out SwfFillEdgeGroup group))
            {
                group = new SwfFillEdgeGroup
                {
                    FillStyleIndex = fillStyleIndex
                };

                shapeData.FillEdgeGroups.Add(group);
                fillGroupIndex[fillStyleIndex] = group;
            }

            group.Edges.Add(edge);
        }

        private void BuildEdgesFromPaths(SwfShapeData shapeData)
        {
            if (shapeData == null || shapeData.Paths == null)
                return;

            shapeData.Edges.Clear();

            // One edge per point gap across every path. Sizing up front avoids the
            // repeated reallocation that dominated parse-time allocation on large
            // vector content.
            int expectedEdges = 0;

            for (int p = 0; p < shapeData.Paths.Count; p++)
            {
                SwfShapePath path = shapeData.Paths[p];

                if (path?.Points != null && path.Points.Count >= 2)
                    expectedEdges += path.Points.Count - 1;
            }

            if (shapeData.Edges.Capacity < expectedEdges)
                shapeData.Edges.Capacity = expectedEdges;

            for (int p = 0; p < shapeData.Paths.Count; p++)
            {
                SwfShapePath path = shapeData.Paths[p];

                if (path == null || path.Points == null || path.Points.Count < 2)
                    continue;

                for (int i = 0; i < path.Points.Count - 1; i++)
                {
                    SwfShapeEdge edge = new SwfShapeEdge
                    {
                        Start = path.Points[i],
                        End = path.Points[i + 1],
                        FillStyle0 = path.FillStyle0,
                        FillStyle1 = path.FillStyle1,
                        LineStyle = path.LineStyle
                    };

                    shapeData.Edges.Add(edge);
                }
            }
        }

        private void BuildFillContours(SwfShapeData shapeData)
        {
            if (shapeData == null || shapeData.FillEdgeGroups == null)
                return;

            for (int g = 0; g < shapeData.FillEdgeGroups.Count; g++)
            {
                SwfFillEdgeGroup group = shapeData.FillEdgeGroups[g];

                group.Contours.Clear();
                int contoursBefore = group.Contours.Count;
                StitchDirectedEdges(shapeData, group);

                // A group that carried edges but yielded no closed loop draws
                // nothing at all. That is a real hole in the output, so it is
                // reported rather than dropped without trace.
                if (group.Contours.Count == contoursBefore && group.Edges.Count > 0)
                {
                    Renderer.SwfRenderDiagnostics.Report(
                        Renderer.SwfRenderProblem.DegenerateGeometry,
                        shapeData.CharacterId,
                        group.FillStyleIndex,
                        "fill group " + group.FillStyleIndex + " has " + group.Edges.Count +
                        " edges but produced no closed contour; nothing is drawn for it");
                }
                MarkHoleCandidates(group);
            }
        }

        // Follows each fill group's edges strictly forward (Start -> End) to form
        // closed loops. BuildFillEdgeGroups already orients every edge so the fill
        // lies on a consistent side (FillStyle1 kept as-is, FillStyle0 reversed),
        // so forward-only traversal preserves the winding: outer boundaries and
        // holes come out with opposite orientation, exactly like Flash. The old
        // code matched either endpoint, which flipped edges and scrambled shapes.
        private void StitchDirectedEdges(SwfShapeData shapeData, SwfFillEdgeGroup group)
        {
            if (group == null || group.Edges == null || group.Edges.Count == 0)
                return;

            // Edges are addressed by index throughout: a struct edge has no identity
            // to hash, and an index-keyed bucket avoids the per-edge hashing the
            // previous HashSet<SwfShapeEdge> performed on every lookup.
            Dictionary<long, List<int>> byStartCell = new Dictionary<long, List<int>>();
            Dictionary<long, List<int>> byEndCell = new Dictionary<long, List<int>>();

            for (int i = 0; i < group.Edges.Count; i++)
            {
                long key = PointCellKey(group.Edges[i].Start);

                if (!byStartCell.TryGetValue(key, out List<int> bucket))
                {
                    bucket = new List<int>();
                    byStartCell[key] = bucket;
                }

                bucket.Add(i);

                long endKey = PointCellKey(group.Edges[i].End);

                if (!byEndCell.TryGetValue(endKey, out List<int> endBucket))
                {
                    endBucket = new List<int>();
                    byEndCell[endKey] = endBucket;
                }

                endBucket.Add(i);
            }

            bool[] used = new bool[group.Edges.Count];

            for (int i = 0; i < group.Edges.Count; i++)
            {
                if (used[i])
                    continue;

                SwfFillContour contour = new SwfFillContour
                {
                    FillStyleIndex = group.FillStyleIndex
                };

                int current = i;
                bool currentReversed = false;
                Vector2 loopStart = group.Edges[current].Start;
                contour.Points.Add(loopStart);

                int guard = 0;
                int maxGuard = group.Edges.Count + 4;

                while (current >= 0 && guard++ < maxGuard)
                {
                    used[current] = true;
                    Vector2 tip = currentReversed
                        ? group.Edges[current].Start
                        : group.Edges[current].End;
                    contour.Points.Add(tip);

                    if (PointsClose(tip, loopStart))
                        break; // loop closed cleanly

                    Vector2 incomingDirection = tip -
                        contour.Points[contour.Points.Count - 2];
                    current = TakeNextForwardEdge(
                        group,
                        byStartCell,
                        used,
                        tip,
                        incomingDirection);
                    currentReversed = false;

                    // Some authoring tools split a fill at a style-change record in
                    // the opposite direction. Flash still welds the endpoints, while
                    // a forward-only walk leaves hundreds of open Isaac contours.
                    // Reverse only as a fallback, preserving normal winding whenever
                    // a correctly directed continuation exists.
                    if (current < 0 && AllowReverseEdgeFallback)
                    {
                        current = TakeNextReverseEdge(
                            group,
                            byEndCell,
                            used,
                            tip,
                            incomingDirection);
                        currentReversed = current >= 0;
                    }
                }

                if (contour.Points.Count >= 3)
                {
                    // A contour whose last point is far from its first could not
                    // be stitched into a closed loop, which means the source edges
                    // have a gap. It is still filled, but the seam is reported.
                    if (!PointsClose(contour.Points[contour.Points.Count - 1], loopStart))
                    {
                        Renderer.SwfRenderDiagnostics.Report(
                            Renderer.SwfRenderProblem.DisconnectedPath,
                            shapeData.CharacterId,
                            group.FillStyleIndex,
                            "fill group " + group.FillStyleIndex +
                            " produced an unclosed contour of " + contour.Points.Count +
                            " points; the outline has a gap");
                    }

                    group.Contours.Add(contour);
                }
            }
        }

        // Returns the index of the next unused edge starting at the tip, or -1.
        private int TakeNextForwardEdge(
            SwfFillEdgeGroup group,
            Dictionary<long, List<int>> byStartCell,
            bool[] used,
            Vector2 tip,
            Vector2 incomingDirection
        )
        {
            long baseX = (long)Mathf.Round(tip.x / PointCellSize);
            long baseY = (long)Mathf.Round(tip.y / PointCellSize);
            int bestCandidate = -1;
            float bestTurn = float.PositiveInfinity;

            // Sub-tolerance rounding can drop a shared vertex into a neighbouring
            // cell, so scan the 3x3 block around the tip, not just its own cell.
            for (long oy = -1; oy <= 1; oy++)
            {
                for (long ox = -1; ox <= 1; ox++)
                {
                    long key = CellKey(baseX + ox, baseY + oy);

                    if (!byStartCell.TryGetValue(key, out List<int> bucket))
                        continue;

                    for (int i = 0; i < bucket.Count; i++)
                    {
                        int candidate = bucket[i];

                        if (used[candidate] ||
                            !PointsClose(group.Edges[candidate].Start, tip))
                        {
                            continue;
                        }

                        Vector2 outgoingDirection =
                            group.Edges[candidate].End - group.Edges[candidate].Start;
                        float turn = RightSideTurn(incomingDirection, outgoingDirection);

                        if (turn < bestTurn)
                        {
                            bestTurn = turn;
                            bestCandidate = candidate;
                        }
                    }
                }
            }

            return bestCandidate;
        }

        private void ParseDefineSceneAndFrameLabelData(SwfTag tag)
        {
            int p = tag.DataStart;
            int end = tag.DataStart + tag.Length;

            try
            {
                uint sceneCount = ReadEncodedU32(ref p, end);

                for (uint i = 0; i < sceneCount; i++)
                {
                    // Scene offsets and names are useful to scene APIs, but AVM1
                    // gotoLabel resolves against the frame-label table below.
                    ReadEncodedU32(ref p, end);
                    ReadNullTerminatedString(ref p, end);
                }

                uint frameLabelCount = ReadEncodedU32(ref p, end);

                for (uint i = 0; i < frameLabelCount; i++)
                {
                    uint frameNumber = ReadEncodedU32(ref p, end);
                    string label = ReadNullTerminatedString(ref p, end);

                    if (!string.IsNullOrEmpty(label))
                    {
                        RootFrameLabels[label] = frameNumber > int.MaxValue
                            ? int.MaxValue
                            : (int)frameNumber;
                    }
                }
            }
            catch (Exception e)
            {
                if (DiagnosticsLogging)
                {
                    Debug.LogWarning(
                        "Failed to parse DefineSceneAndFrameLabelData at " +
                        tag.DataStart + ": " + e.Message
                    );
                }
            }
        }

        private uint ReadEncodedU32(ref int p, int end)
        {
            uint value = 0;

            for (int byteIndex = 0; byteIndex < 5; byteIndex++)
            {
                if (p >= end)
                    throw new EndOfStreamException("Truncated EncodedU32 value.");

                byte current = data[p++];

                if (byteIndex == 4)
                {
                    // EncodedU32 has at most 32 payload bits. Only the low four
                    // bits of a fifth byte belong to the value.
                    value |= (uint)(current & 0x0F) << 28;

                    if ((current & 0x80) != 0 || (current & 0x70) != 0)
                        throw new InvalidDataException("EncodedU32 value is too large.");

                    return value;
                }

                value |= (uint)(current & 0x7F) << (byteIndex * 7);

                if ((current & 0x80) == 0)
                    return value;
            }

            throw new InvalidDataException("Invalid EncodedU32 value.");
        }

        private int TakeNextReverseEdge(
            SwfFillEdgeGroup group,
            Dictionary<long, List<int>> byEndCell,
            bool[] used,
            Vector2 tip,
            Vector2 incomingDirection)
        {
            long baseX = (long)Mathf.Round(tip.x / PointCellSize);
            long baseY = (long)Mathf.Round(tip.y / PointCellSize);
            int bestCandidate = -1;
            float bestTurn = float.PositiveInfinity;

            for (long oy = -1; oy <= 1; oy++)
            {
                for (long ox = -1; ox <= 1; ox++)
                {
                    long key = CellKey(baseX + ox, baseY + oy);

                    if (!byEndCell.TryGetValue(key, out List<int> bucket))
                        continue;

                    for (int i = 0; i < bucket.Count; i++)
                    {
                        int candidate = bucket[i];

                        if (!used[candidate] &&
                            PointsClose(group.Edges[candidate].End, tip))
                        {
                            Vector2 outgoingDirection =
                                group.Edges[candidate].Start - group.Edges[candidate].End;
                            float turn = RightSideTurn(incomingDirection, outgoingDirection);

                            if (turn < bestTurn)
                            {
                                bestTurn = turn;
                                bestCandidate = candidate;
                            }
                        }
                    }
                }
            }

            return bestCandidate;
        }

        private static float RightSideTurn(Vector2 incoming, Vector2 outgoing)
        {
            if (incoming.sqrMagnitude <= 0.0000001f ||
                outgoing.sqrMagnitude <= 0.0000001f)
            {
                return float.PositiveInfinity;
            }

            // SWF coordinates have Y pointing down. For edges oriented with their
            // fill on the right, the correct continuation at a shared vertex is the
            // smallest positive turn in this coordinate system. Selecting the first
            // edge in file order crossed unrelated lobes whenever several contours
            // touched the same point, producing self-intersections and giant fans.
            float incomingAngle = Mathf.Atan2(incoming.y, incoming.x);
            float outgoingAngle = Mathf.Atan2(outgoing.y, outgoing.x);
            return Mathf.Repeat(outgoingAngle - incomingAngle, Mathf.PI * 2f);
        }

        // Vertex-welding grid for edge stitching. Matches the PointsClose radius so
        // two endpoints that count as "the same" always land within a 3x3 lookup.
        private const float PointCellSize = 0.5f;
        private const bool AllowReverseEdgeFallback = true;

        private static long PointCellKey(Vector2 p)
        {
            long qx = (long)Mathf.Round(p.x / PointCellSize);
            long qy = (long)Mathf.Round(p.y / PointCellSize);
            return CellKey(qx, qy);
        }

        private static long CellKey(long qx, long qy)
        {
            return (qx << 32) ^ (qy & 0xffffffffL);
        }

        private void MarkHoleCandidates(SwfFillEdgeGroup group)
        {
            if (group == null || group.Contours == null)
                return;

            for (int i = 0; i < group.Contours.Count; i++)
            {
                SwfFillContour contour = group.Contours[i];
                contour.IsHoleCandidate = false;

                if (contour.Points == null || contour.Points.Count < 3)
                    continue;

                Vector2 center = GetContourCenter(contour);

                for (int j = 0; j < group.Contours.Count; j++)
                {
                    SwfFillContour other = group.Contours[j];

                    if (other == contour || other == null || other.Points == null || other.Points.Count < 3)
                        continue;

                    bool smaller = Mathf.Abs(contour.Area) < Mathf.Abs(other.Area);
                    bool inside = PointInPolygon(center, other.Points);

                    if (smaller && inside)
                    {
                        contour.IsHoleCandidate = true;
                        break;
                    }
                }
            }
        }

        private Vector2 GetContourCenter(SwfFillContour contour)
        {
            Vector2 sum = Vector2.zero;

            for (int i = 0; i < contour.Points.Count; i++)
            {
                sum += contour.Points[i];
            }

            return sum / contour.Points.Count;
        }

        private bool PointInPolygon(Vector2 point, List<Vector2> polygon)
        {
            bool inside = false;

            for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
            {
                Vector2 pi = polygon[i];
                Vector2 pj = polygon[j];

                bool intersect =
                    ((pi.y > point.y) != (pj.y > point.y)) &&
                    (point.x < (pj.x - pi.x) * (point.y - pi.y) / ((pj.y - pi.y) + 0.00001f) + pi.x);

                if (intersect)
                    inside = !inside;
            }

            return inside;
        }

        private void ParseDefineText(SwfTag tag)
        {
            try
            {
                int p = tag.DataStart;

                ushort characterId = ReadUInt16LEAt(p);
                p += 2;

                SwfRect bounds = ReadRectAt(p, out int afterBounds);
                p = afterBounds;

                SwfMatrix textMatrix = ReadMatrixAt(p, out int afterMatrix);
                p = afterMatrix;

                byte glyphBits = data[p++];
                byte advanceBits = data[p++];

                DefineTextTag text = new DefineTextTag
                {
                    CharacterId = characterId,
                    TextBounds = bounds,
                    TextMatrix = textMatrix,
                    GlyphBits = glyphBits,
                    AdvanceBits = advanceBits
                };

                ParseTextRecords(text, p, tag.DataStart + tag.Length, tag.Code == 33);

                Texts.Add(text);

                if (VerboseLogging)
                {
                    Debug.Log(text.ToString());

                    for (int i = 0; i < text.Records.Count; i++)
                    {
                        SwfTextRecord record = text.Records[i];

                        Debug.Log("  " + record.ToString());

                        string decoded = DecodeTextRecord(record);
                        Debug.Log("  Decoded Text: \"" + decoded + "\"");

                        int max = System.Math.Min(10, record.GlyphEntries.Count);
                        for (int g = 0; g < max; g++)
                        {
                            SwfGlyphEntry entry = record.GlyphEntries[g];

                            char ch = '?';

                            DefineFont3Tag font = FindFont3ById(record.FontId);

                            if (font != null && entry.GlyphIndex >= 0 && entry.GlyphIndex < font.CodeTable.Count)
                            {
                                ch = (char)font.CodeTable[entry.GlyphIndex];
                            }

                            Debug.Log(
                                "    Glyph Index=" + entry.GlyphIndex +
                                " Char='" + ch +
                                "' Advance=" + entry.GlyphAdvance
                            );
                        }
                    }
                }
            }
            catch (System.Exception e)
            {
                if (DiagnosticsLogging)
                    Debug.LogWarning("Failed to parse DefineText at " + tag.DataStart + ": " + e.Message);
            }
        }

        private void ParseDeferredShape(DeferredShapeInfo deferred)
        {
            if (deferred?.Shape == null || deferred.Shape.ShapeData != null)
                return;

            SwfShapeData shapeData = ParseShapeStylesOnly(
                deferred.Shape.CharacterId,
                deferred.StylesOffset,
                deferred.ShapeVersion,
                out int afterStyles);
            ParseShapeRecords(shapeData, afterStyles);
            BuildEdgesFromPaths(shapeData);
            BuildFillEdgeGroups(shapeData);
            BuildFillContours(shapeData);
            deferred.Shape.ShapeData = shapeData;

            if (VerboseLogging)
            {
                Debug.Log(deferred.Shape.ToString());
                Debug.Log(shapeData.ToString());
            }
        }

        public void EnsureAllShapesParsed()
        {
            for (int i = 0; i < Shapes.Count; i++)
                EnsureShapeParsed(Shapes[i]);
        }

        private void EnsureShapeParsed(DefineShapeTag shape)
        {
            if (shape == null || shape.ShapeData != null)
                return;

            if (!deferredShapes.TryGetValue(shape.CharacterId, out DeferredShapeInfo deferred))
                return;

            try
            {
                ParseDeferredShape(deferred);
                deferredShapes.Remove(shape.CharacterId);
            }
            catch (Exception e)
            {
                deferredShapes.Remove(shape.CharacterId);

                if (DiagnosticsLogging)
                    Debug.LogWarning("Failed to lazily parse DefineShape " +
                        shape.CharacterId + ": " + e.Message);
            }
        }

        private SwfSoundStreamHead ParseSoundStreamHeadFromTag(SwfTag tag)
        {
            if (tag == null || tag.Length < 4)
                return null;

            int p = tag.DataStart;
            byte playbackFlags = data[p++];
            byte streamFlags = data[p++];
            SwfSoundStreamHead head = new SwfSoundStreamHead
            {
                PlaybackSampleRate = GetSoundSampleRate((playbackFlags >> 2) & 0x03),
                PlaybackIs16Bit = (playbackFlags & 0x02) != 0,
                PlaybackIsStereo = (playbackFlags & 0x01) != 0,
                StreamFormat = (streamFlags >> 4) & 0x0F,
                StreamSampleRate = GetSoundSampleRate((streamFlags >> 2) & 0x03),
                StreamIs16Bit = (streamFlags & 0x02) != 0,
                StreamIsStereo = (streamFlags & 0x01) != 0,
                SamplesPerFrame = ReadUInt16LEAt(p)
            };
            p += 2;

            if (head.StreamFormat == 2 && p + 2 <= tag.DataStart + tag.Length)
                head.LatencySeek = (short)ReadUInt16LEAt(p);

            return head;
        }

        private SwfSoundStreamBlock ParseSoundStreamBlockFromTag(
            SwfTag tag,
            SwfSoundStreamHead head,
            int frameIndex)
        {
            if (tag == null)
                return null;

            int p = tag.DataStart;
            int end = tag.DataStart + tag.Length;
            SwfSoundStreamBlock block = new SwfSoundStreamBlock { FrameIndex = frameIndex };

            if (head != null && head.StreamFormat == 2 && p + 4 <= end)
            {
                block.SampleCount = ReadUInt16LEAt(p);
                p += 2;
                block.SeekSamples = (short)ReadUInt16LEAt(p);
                p += 2;
            }

            int length = Math.Max(0, end - p);

            if (length == 0)
                return null;

            block.Data = new byte[length];
            Array.Copy(data, p, block.Data, 0, length);
            return block;
        }

        private void ParseDefineEditText(SwfTag tag)
        {
            try
            {
                int p = tag.DataStart;
                int end = tag.DataStart + tag.Length;
                DefineEditTextTag text = new DefineEditTextTag
                {
                    CharacterId = ReadUInt16LEAt(p)
                };
                p += 2;
                text.Bounds = ReadRectAt(p, out p);
                text.Flags = ReadUInt16LEAt(p);
                p += 2;

                if (text.HasFont)
                {
                    text.FontId = ReadUInt16LEAt(p);
                    p += 2;
                }

                if (text.HasFontClass)
                    text.FontClass = ReadNullTerminatedString(ref p, end);

                if (text.HasFont)
                {
                    text.FontHeight = ReadUInt16LEAt(p);
                    p += 2;
                }

                if (text.HasTextColor)
                {
                    text.Color = new Color(
                        data[p] / 255f,
                        data[p + 1] / 255f,
                        data[p + 2] / 255f,
                        data[p + 3] / 255f
                    );
                    p += 4;
                }

                if (text.HasMaxLength)
                {
                    text.MaxLength = ReadUInt16LEAt(p);
                    p += 2;
                }

                if (text.HasLayout)
                {
                    text.Alignment = data[p++];
                    text.LeftMargin = ReadUInt16LEAt(p);
                    p += 2;
                    text.RightMargin = ReadUInt16LEAt(p);
                    p += 2;
                    text.Indent = ReadUInt16LEAt(p);
                    p += 2;
                    text.Leading = (short)ReadUInt16LEAt(p);
                    p += 2;
                }

                text.VariableName = ReadNullTerminatedString(ref p, end);

                if (text.HasText && p < end)
                    text.InitialText = ReadNullTerminatedString(ref p, end);

                EditTexts.Add(text);
            }
            catch (System.Exception e)
            {
                if (DiagnosticsLogging)
                    Debug.LogWarning("Failed to parse DefineEditText at " +
                        tag.DataStart + ": " + e.Message);
            }
        }

        private void ParseTextRecords(DefineTextTag text, int offset, int end, bool useRgba)
        {
            int p = offset;

            while (p < end)
            {
                byte recordHeader = data[p++];

                if (recordHeader == 0)
                    break;

                bool isTextRecord = (recordHeader & 0x80) != 0;

                if (!isTextRecord)
                {
                    if (DiagnosticsLogging)
                        Debug.LogWarning("Unknown text record header: " + recordHeader);
                    break;
                }

                SwfTextRecord record = new SwfTextRecord
                {
                    HasFont = (recordHeader & 0x08) != 0,
                    HasColor = (recordHeader & 0x04) != 0,
                    HasYOffset = (recordHeader & 0x02) != 0,
                    HasXOffset = (recordHeader & 0x01) != 0
                };

                if (record.HasFont)
                {
                    record.FontId = ReadUInt16LEAt(p);
                    p += 2;
                }

                if (record.HasColor)
                {
                    byte r = data[p++];
                    byte g = data[p++];
                    byte b = data[p++];
                    byte a = useRgba ? data[p++] : (byte)255;

                    record.Color = new Color(r / 255f, g / 255f, b / 255f, a / 255f);
                }

                if (record.HasXOffset)
                {
                    record.XOffset = (short)ReadUInt16LEAt(p);
                    p += 2;
                }

                if (record.HasYOffset)
                {
                    record.YOffset = (short)ReadUInt16LEAt(p);
                    p += 2;
                }

                if (record.HasFont)
                {
                    record.TextHeight = ReadUInt16LEAt(p);
                    p += 2;
                }

                byte glyphCount = data[p++];

                SwfBitReader reader = new SwfBitReader(data, p);

                for (int i = 0; i < glyphCount; i++)
                {
                    int glyphIndex = (int)reader.ReadUBits(text.GlyphBits);
                    int glyphAdvance = reader.ReadSBits(text.AdvanceBits);

                    record.GlyphEntries.Add(new SwfGlyphEntry
                    {
                        GlyphIndex = glyphIndex,
                        GlyphAdvance = glyphAdvance
                    });
                }

                reader.AlignToByte();
                p = reader.BytePosition;

                text.Records.Add(record);
            }
        }

        private void ParseDefineFont3(SwfTag tag)
        {
            try
            {
                int p = tag.DataStart;

                ushort fontId = ReadUInt16LEAt(p);
                p += 2;

                byte flags = data[p++];

                bool hasLayout = (flags & 0x80) != 0;
                bool shiftJis = (flags & 0x40) != 0;
                bool smallText = (flags & 0x20) != 0;
                bool ansi = (flags & 0x10) != 0;
                bool wideOffsets = (flags & 0x08) != 0;
                bool wideCodes = (flags & 0x04) != 0;
                bool italic = (flags & 0x02) != 0;
                bool bold = (flags & 0x01) != 0;

                byte languageCode = data[p++];

                byte fontNameLength = data[p++];

                string fontName = System.Text.Encoding.ASCII.GetString(data, p, fontNameLength);
                p += fontNameLength;

                // DefineFont2/DefineFont3 has NumGlyphs here.
                ushort numGlyphs = ReadUInt16LEAt(p);
                p += 2;

                int offsetTableStart = p;
                int offsetSize = wideOffsets ? 4 : 2;

                // Skip OffsetTable
                p += numGlyphs * offsetSize;

                // Read CodeTableOffset
                uint codeTableOffset;

                if (wideOffsets)
                {
                    codeTableOffset = ReadUInt32LEAt(p);
                    p += 4;
                }
                else
                {
                    codeTableOffset = ReadUInt16LEAt(p);
                    p += 2;
                }

                int glyphShapeTableStart = offsetTableStart;

                int codeTableStart = offsetTableStart + (int)codeTableOffset;

                DefineFont3Tag font = new DefineFont3Tag
                {
                    FontId = fontId,
                    HasLayout = hasLayout,
                    ShiftJis = shiftJis,
                    SmallText = smallText,
                    Ansi = ansi,
                    WideOffsets = wideOffsets,
                    WideCodes = wideCodes,
                    Italic = italic,
                    Bold = bold,
                    LanguageCode = languageCode,
                    FontName = fontName,
                    GlyphCount = numGlyphs
                };
                if (FontDisplayNames.TryGetValue(fontId, out string displayName) &&
                    !string.IsNullOrEmpty(displayName))
                {
                    font.FontName = displayName;
                }

                p = codeTableStart;

                for (int i = 0; i < numGlyphs; i++)
                {
                    int code;

                    if (wideCodes)
                    {
                        code = ReadUInt16LEAt(p);
                        p += 2;
                    }
                    else
                    {
                        code = data[p++];
                    }

                    font.CodeTable.Add(code);
                }

                Fonts3.Add(font);

                if (VerboseLogging)
                {
                    Debug.Log(font.ToString());

                    int max = System.Math.Min(30, font.CodeTable.Count);

                    for (int i = 0; i < max; i++)
                    {
                        int code = font.CodeTable[i];

                        Debug.Log(
                            "  Font glyph " + i +
                            " -> code " + code +
                            " char '" + (char)code + "'"
                        );
                    }
                }
            }
            catch (System.Exception e)
            {
                if (DiagnosticsLogging)
                    Debug.LogWarning("Failed to parse DefineFont3 at " + tag.DataStart + ": " + e.Message);
            }
        }

        private void ParseDefineFontName(SwfTag tag)
        {
            try
            {
                int end = tag.DataStart + tag.Length;
                int p = tag.DataStart;
                EnsureTagBytes(p, 2, end, "font id");
                ushort fontId = ReadUInt16LEAt(p);
                p += 2;

                string displayName = ReadNullTerminatedString(ref p, end);
                // Copyright string follows. It is intentionally consumed so a
                // malformed tag is diagnosed, although it is not needed to render.
                ReadNullTerminatedString(ref p, end);
                FontDisplayNames[fontId] = displayName;

                for (int i = 0; i < Fonts3.Count; i++)
                {
                    if (Fonts3[i].FontId == fontId && !string.IsNullOrEmpty(displayName))
                        Fonts3[i].FontName = displayName;
                }
            }
            catch (Exception e)
            {
                if (DiagnosticsLogging)
                    Debug.LogWarning("Failed to parse DefineFontName at " + tag.DataStart + ": " + e.Message);
            }
        }

        private void ParseDefineFontAlignZones(SwfTag tag)
        {
            try
            {
                int end = tag.DataStart + tag.Length;
                int p = tag.DataStart;
                EnsureTagBytes(p, 3, end, "font alignment zone header");
                ushort fontId = ReadUInt16LEAt(p);
                p += 2;
                int csmTableHint = (data[p++] >> 6) & 0x03;

                byte[] zoneTable = new byte[end - p];
                if (zoneTable.Length > 0)
                    Buffer.BlockCopy(data, p, zoneTable, 0, zoneTable.Length);

                FontAlignZones[fontId] = new SwfFontAlignZones
                {
                    FontId = fontId,
                    CsmTableHint = csmTableHint,
                    EncodedZoneTable = zoneTable
                };
            }
            catch (Exception e)
            {
                if (DiagnosticsLogging)
                    Debug.LogWarning("Failed to parse DefineFontAlignZones at " + tag.DataStart + ": " + e.Message);
            }
        }

        private void ParseCsmTextSettings(SwfTag tag)
        {
            try
            {
                int end = tag.DataStart + tag.Length;
                int p = tag.DataStart;
                EnsureTagBytes(p, 12, end, "CSM text settings");
                ushort textId = ReadUInt16LEAt(p);
                p += 2;
                byte flags = data[p++];

                SwfCsmTextSettings settings = new SwfCsmTextSettings
                {
                    TextId = textId,
                    UseFlashType = (flags >> 6) & 0x01,
                    GridFit = (flags >> 3) & 0x03,
                    Thickness = BitConverter.ToSingle(data, p),
                    Sharpness = BitConverter.ToSingle(data, p + 4)
                };
                CsmTextSettings[textId] = settings;
            }
            catch (Exception e)
            {
                if (DiagnosticsLogging)
                    Debug.LogWarning("Failed to parse CSMTextSettings at " + tag.DataStart + ": " + e.Message);
            }
        }

        public DefineFont3Tag FindFont3ById(ushort fontId)
        {
            if (font3Index == null)
            {
                font3Index = new Dictionary<ushort, DefineFont3Tag>(Fonts3.Count);
                for (int i = 0; i < Fonts3.Count; i++)
                    font3Index[Fonts3[i].FontId] = Fonts3[i];
            }

            return font3Index.TryGetValue(fontId, out DefineFont3Tag font) ? font : null;
        }

        private string DecodeTextRecord(SwfTextRecord record)
        {
            DefineFont3Tag font = FindFont3ById(record.FontId);

            if (font == null)
                return "[Missing Font " + record.FontId + "]";

            System.Text.StringBuilder builder = new System.Text.StringBuilder();

            for (int i = 0; i < record.GlyphEntries.Count; i++)
            {
                int glyphIndex = record.GlyphEntries[i].GlyphIndex;

                if (glyphIndex >= 0 && glyphIndex < font.CodeTable.Count)
                {
                    int code = font.CodeTable[glyphIndex];
                    builder.Append((char)code);
                }
                else
                {
                    builder.Append('?');
                }
            }

            return builder.ToString();
        }

        private uint ReadUInt32LEAt(int offset)
        {
            return System.BitConverter.ToUInt32(data, offset);
        }

        private bool PointsClose(Vector2 a, Vector2 b)
        {
            return Vector2.Distance(a, b) < 0.5f;
        }

        public DefineTextTag FindTextById(ushort id)
        {
            if (textIndex == null)
            {
                textIndex = new Dictionary<ushort, DefineTextTag>(Texts.Count);
                for (int i = 0; i < Texts.Count; i++)
                    textIndex[Texts[i].CharacterId] = Texts[i];
            }

            return textIndex.TryGetValue(id, out DefineTextTag text) ? text : null;
        }

        public DefineEditTextTag FindEditTextById(ushort id)
        {
            if (editTextIndex == null)
            {
                editTextIndex = new Dictionary<ushort, DefineEditTextTag>(EditTexts.Count);
                for (int i = 0; i < EditTexts.Count; i++)
                    editTextIndex[EditTexts[i].CharacterId] = EditTexts[i];
            }

            return editTextIndex.TryGetValue(id, out DefineEditTextTag text) ? text : null;
        }
        public string DecodeTextRecordPublic(SwfTextRecord record)
        {
            return DecodeTextRecord(record);
        }

        private sealed class DeferredShapeInfo
        {
            public DefineShapeTag Shape;
            public int StylesOffset;
            public int ShapeVersion;
        }

        private struct RectInfo
        {
            public int XMin;
            public int XMax;
            public int YMin;
            public int YMax;

            public int WidthTwips => XMax - XMin;
            public int HeightTwips => YMax - YMin;

            public int NextBytePosition;
        }
    }
}
