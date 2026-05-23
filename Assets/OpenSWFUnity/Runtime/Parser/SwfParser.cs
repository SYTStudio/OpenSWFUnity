using System;
using System.IO;
using System.IO.Compression;
using System.Collections.Generic;
using OpenSWFUnity.Runtime.Tags;
using UnityEngine;

namespace OpenSWFUnity.Runtime.Parser
{
    public class SwfParser
    {
        private byte[] data;
        private int position;
        public bool VerboseLogging = false;

        public SwfHeader Header { get; private set; }

        public List<SwfTag> Tags { get; private set; } = new List<SwfTag>();

        public System.Collections.Generic.List<DefineShapeTag> Shapes { get; private set; } = new System.Collections.Generic.List<DefineShapeTag>();
        public System.Collections.Generic.List<PlaceObject2Tag> PlacedObjects { get; private set; } = new System.Collections.Generic.List<PlaceObject2Tag>();
        public System.Collections.Generic.List<DefineSpriteTag> Sprites { get; private set; } = new System.Collections.Generic.List<DefineSpriteTag>();
        public System.Collections.Generic.List<DefineTextTag> Texts { get; private set; } = new System.Collections.Generic.List<DefineTextTag>();
        public System.Collections.Generic.List<DefineFont3Tag> Fonts3 { get; private set; } = new System.Collections.Generic.List<DefineFont3Tag>();

        public SetBackgroundColorTag BackgroundColor { get; private set; }


        public int DefineFontCount;
        public int DefineFont2Count;
        public int DefineFont3Count;
        public int DefineTextCount;
        public int DefineText2Count;
        public int DefineEditTextCount;
        public int CsmTextSettingsCount;
        public int FontAlignZonesCount;

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

            if (result.Length != uncompressedLength)
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

            Tags.Clear();

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
                    Debug.LogError("Tag read past end of file at tag: " + tag.Name);
                    break;
                }
            }

            return Tags;
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

                    Debug.Log(BackgroundColor.ToString());
                }
            }

            if (tag.Code == 2 || tag.Code == 22 || tag.Code == 32 || tag.Code == 83)
            {
                ParseDefineShape(tag);
            }

            if (tag.Code == 26)
            {
                ParsePlaceObject2(tag);
            }

            if (tag.Code == 39)
            {
                ParseDefineSprite(tag);
            }

            if (tag.Code == 11)
            {
                ParseDefineText(tag);
            }

            if (tag.Code == 75)
            {
                ParseDefineFont3(tag);
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

                    p += innerLength;
                }

                Sprites.Add(sprite);

                Debug.Log(
                    "Parsed Sprite Timeline. SpriteId=" + sprite.SpriteId +
                    " DeclaredFrames=" + sprite.FrameCount +
                    " ParsedFrames=" + sprite.Frames.Count +
                    " ControlTags=" + sprite.ControlTags.Count
                );

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
                Debug.LogWarning("Failed to parse DefineSprite at " + tag.DataStart + ": " + e.Message);
            }
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

                SwfShapeData shapeData = ParseShapeStylesOnly(
                    characterId,
                    p,
                    out int afterStyles
                );

                ParseShapeRecords(shapeData, afterStyles);

                BuildEdgesFromPaths(shapeData);
                BuildFillEdgeGroups(shapeData);
                BuildFillContours(shapeData);

                DefineShapeTag shape = new DefineShapeTag
                {
                    CharacterId = characterId,
                    ShapeBounds = bounds,
                    ShapeData = shapeData
                };

                Shapes.Add(shape);

                if (VerboseLogging)
                {
                    Debug.Log(shape.ToString());
                    Debug.Log(shapeData.ToString());
                }
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("Failed to parse DefineShape at " + tag.DataStart + ": " + e.Message);
            }
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
                Debug.LogWarning("Failed to parse PlaceObject2 at " + tag.DataStart + ": " + e.Message);
            }
        }

        public DefineShapeTag FindShapeById(ushort id)
        {
            for (int i = 0; i < Shapes.Count; i++)
            {
                if (Shapes[i].CharacterId == id)
                    return Shapes[i];
            }

            return null;
        }

        public DefineSpriteTag FindSpriteById(ushort id)
        {
            for (int i = 0; i < Sprites.Count; i++)
            {
                if (Sprites[i].SpriteId == id)
                    return Sprites[i];
            }

            return null;
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
                HasMove = (flags & 0x01) != 0
            };

            place.Depth = ReadUInt16LEAt(p);
            p += 2;

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

            return place;
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

        private SwfShapeData ParseShapeStylesOnly(ushort characterId, int offset, out int afterStylesPosition)
        {
            int p = offset;

            SwfShapeData shapeData = new SwfShapeData
            {
                CharacterId = characterId
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
                p = ReadFillStyle(p, shapeData);
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
                p = SkipLineStyle(p);
            }

            SwfBitReader reader = new SwfBitReader(data, p);

            shapeData.NumFillBits = (int)reader.ReadUBits(4);
            shapeData.NumLineBits = (int)reader.ReadUBits(4);

            reader.AlignToByte();

            afterStylesPosition = reader.BytePosition;

            return shapeData;
        }

        private int ReadFillStyle(int p, SwfShapeData shapeData)
        {
            SwfFillStyle fill = new SwfFillStyle();

            fill.FillType = data[p];
            p++;

            // 0x00 = solid fill RGB for DefineShape v1
            if (fill.FillType == 0x00)
            {
                fill.R = data[p];
                fill.G = data[p + 1];
                fill.B = data[p + 2];
                fill.A = 255;

                p += 3;

                shapeData.FillStyles.Add(fill);
            }
            // Gradient fill
            else if (fill.FillType == 0x10 || fill.FillType == 0x12 || fill.FillType == 0x13)
            {
                p = SkipMatrix(p);

                byte gradientInfo = data[p];
                p++;

                int numGradients = gradientInfo & 0x0F;

                // ratio + RGB
                p += numGradients * 4;

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
                p += 2; // bitmapId
                p = SkipMatrix(p);

                shapeData.FillStyles.Add(fill);
            }
            else
            {
                UnityEngine.Debug.LogWarning("Unknown FillStyleType: 0x" + fill.FillType.ToString("X2"));
                shapeData.FillStyles.Add(fill);
            }

            return p;
        }

        private int SkipLineStyle(int p)
        {
            p += 2; // width

            // DefineShape line style uses RGB = 3 bytes.
            p += 3;

            return p;
        }

        private int SkipMatrix(int offset)
        {
            ReadMatrixAt(offset, out int afterMatrix);
            return afterMatrix;
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
                        currentFillStyle0 = (int)reader.ReadUBits(numFillBits);
                    }

                    if (stateFillStyle1)
                    {
                        currentFillStyle1 = (int)reader.ReadUBits(numFillBits);
                    }

                    if (stateLineStyle)
                    {
                        currentLineStyle = (int)reader.ReadUBits(numLineBits);
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
                        // New styles inside shape records are not supported yet.
                        break;
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

                        AddShapeEdge(
                            shapeData,
                            startX,
                            startY,
                            endX,
                            endY,
                            currentPath.FillStyle0,
                            currentPath.FillStyle1,
                            currentPath.LineStyle
                        );

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

                        // Quadratic Bezier sampling
                        const int steps = 8;

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

                            SwfShapeEdge edge = new SwfShapeEdge
                            {
                                Start = previousPoint,
                                End = sampledPoint,
                                FillStyle0 = currentPath.FillStyle0,
                                FillStyle1 = currentPath.FillStyle1,
                                LineStyle = currentPath.LineStyle
                            };

                            shapeData.Edges.Add(edge);

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

        private void AddShapeEdge(
            SwfShapeData shapeData,
            int startX,
            int startY,
            int endX,
            int endY,
            int fillStyle0,
            int fillStyle1,
            int lineStyle
        )
        {
            SwfShapeEdge edge = new SwfShapeEdge
            {
                Start = new Vector2(startX / 20f, startY / 20f),
                End = new Vector2(endX / 20f, endY / 20f),
                FillStyle0 = fillStyle0,
                FillStyle1 = fillStyle1,
                LineStyle = lineStyle
            };

            shapeData.Edges.Add(edge);
        }

        private void BuildFillEdgeGroups(SwfShapeData shapeData)
        {
            if (shapeData == null || shapeData.Edges == null)
                return;

            shapeData.FillEdgeGroups.Clear();

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
                    SwfShapeEdge reversed = new SwfShapeEdge
                    {
                        Start = edge.End,
                        End = edge.Start,
                        FillStyle0 = edge.FillStyle0,
                        FillStyle1 = edge.FillStyle1,
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

        private void AddEdgeToFillGroup(
            SwfShapeData shapeData,
            int fillStyleIndex,
            SwfShapeEdge edge
        )
        {
            SwfFillEdgeGroup group = null;

            for (int i = 0; i < shapeData.FillEdgeGroups.Count; i++)
            {
                if (shapeData.FillEdgeGroups[i].FillStyleIndex == fillStyleIndex)
                {
                    group = shapeData.FillEdgeGroups[i];
                    break;
                }
            }

            if (group == null)
            {
                group = new SwfFillEdgeGroup
                {
                    FillStyleIndex = fillStyleIndex
                };

                shapeData.FillEdgeGroups.Add(group);
            }

            group.Edges.Add(edge);
        }

        private void BuildEdgesFromPaths(SwfShapeData shapeData)
        {
            if (shapeData == null || shapeData.Paths == null)
                return;

            shapeData.Edges.Clear();

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

                List<SwfShapeEdge> remaining = new List<SwfShapeEdge>(group.Edges);

                while (remaining.Count > 0)
                {
                    SwfShapeEdge startEdge = remaining[0];
                    remaining.RemoveAt(0);

                    SwfFillContour contour = new SwfFillContour
                    {
                        FillStyleIndex = group.FillStyleIndex
                    };

                    contour.Points.Add(startEdge.Start);
                    contour.Points.Add(startEdge.End);

                    Vector2 currentEnd = startEdge.End;

                    bool foundNext = true;
                    int guard = 0;

                    while (foundNext && guard < 10000)
                    {
                        guard++;
                        foundNext = false;

                        for (int i = 0; i < remaining.Count; i++)
                        {
                            SwfShapeEdge edge = remaining[i];

                            if (PointsClose(edge.Start, currentEnd))
                            {
                                contour.Points.Add(edge.End);
                                currentEnd = edge.End;
                                remaining.RemoveAt(i);
                                foundNext = true;
                                break;
                            }

                            if (PointsClose(edge.End, currentEnd))
                            {
                                contour.Points.Add(edge.Start);
                                currentEnd = edge.Start;
                                remaining.RemoveAt(i);
                                foundNext = true;
                                break;
                            }
                        }

                        if (PointsClose(currentEnd, contour.Points[0]))
                        {
                            break;
                        }
                    }

                    group.Contours.Add(contour);
                }

                MarkHoleCandidates(group);
            }
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

                ParseTextRecords(text, p, tag.DataStart + tag.Length);

                Texts.Add(text);

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
            catch (System.Exception e)
            {
                Debug.LogWarning("Failed to parse DefineText at " + tag.DataStart + ": " + e.Message);
            }
        }

        private void ParseTextRecords(DefineTextTag text, int offset, int end)
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

                    record.Color = new Color(r / 255f, g / 255f, b / 255f, 1f);
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

                // Important: DefineFont2/DefineFont3 has NumGlyphs here.
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
            catch (System.Exception e)
            {
                Debug.LogWarning("Failed to parse DefineFont3 at " + tag.DataStart + ": " + e.Message);
            }
        }

        public DefineFont3Tag FindFont3ById(ushort fontId)
        {
            for (int i = 0; i < Fonts3.Count; i++)
            {
                if (Fonts3[i].FontId == fontId)
                    return Fonts3[i];
            }

            return null;
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
            for (int i = 0; i < Texts.Count; i++)
            {
                if (Texts[i].CharacterId == id)
                    return Texts[i];
            }

            return null;
        }

        public string DecodeTextRecordPublic(SwfTextRecord record)
        {
            return DecodeTextRecord(record);
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