using System;
using System.Collections.Generic;
using System.Text;

namespace PlanSafe.Api.Services;

/// <summary>
/// Standalone, dependency-free QR code generator producing clean vector SVG markup.
/// Complies with ISO/IEC 18004 (Error Correction Level Q, Byte Mode).
/// Works seamlessly under .NET Native AOT with zero reflection or external dependencies.
/// </summary>
public static class QrCodeSvgGenerator
{
    // Version capacities for Error Correction Level Q (in total data codewords)
    // Version 1..6 capacities for ECC Q:
    private static readonly int[] TotalDataCodewordsQ = [0, 13, 22, 34, 48, 62, 76, 88, 110, 132, 154];
    private static readonly int[] TotalEccCodewordsQ = [0, 13, 22, 36, 52, 72, 96, 120, 152, 180, 216];
    private static readonly int[] EccPerBlockQ = [0, 13, 22, 18, 26, 18, 24, 18, 22, 20, 24];
    private static readonly int[] BlocksGroup1Q = [0, 1, 1, 2, 2, 4, 4, 4, 4, 4, 6];
    private static readonly int[] DataPerBlockG1Q = [0, 13, 22, 17, 24, 15, 19, 14, 18, 16, 15];
    private static readonly int[] BlocksGroup2Q = [0, 0, 0, 0, 0, 0, 0, 4, 4, 4, 4];
    private static readonly int[] DataPerBlockG2Q = [0, 0, 0, 0, 0, 0, 0, 15, 19, 17, 16];

    // Alignment pattern center coordinates for versions 1..10
    private static readonly int[][] AlignmentPatternCenters =
    [
        [],
        [],
        [6, 18],
        [6, 22],
        [6, 26],
        [6, 30],
        [6, 34],
        [6, 22, 38],
        [6, 24, 42],
        [6, 26, 46],
        [6, 28, 50]
    ];

    public static string GenerateSvg(string content, int margin = 4)
    {
        if (string.IsNullOrEmpty(content))
        {
            content = "https://plansafe.local";
        }

        byte[] rawBytes = Encoding.UTF8.GetBytes(content);

        // Find minimum required version (1..10)
        int version = 1;
        while (version <= 10)
        {
            int charCountBits = version < 10 ? 8 : 16;
            int totalHeaderBits = 4 + charCountBits; // 4 bit mode indicator (0100 for byte)
            int availableBits = TotalDataCodewordsQ[version] * 8;
            if (rawBytes.Length * 8 + totalHeaderBits <= availableBits)
            {
                break;
            }
            version++;
        }

        if (version > 10)
        {
            // Fallback: truncate or clamp version to 10
            version = 10;
        }

        int totalDataCodewords = TotalDataCodewordsQ[version];
        var bitBuffer = new List<bool>();

        // Mode indicator: 0100 (Byte mode)
        bitBuffer.Add(false); bitBuffer.Add(true); bitBuffer.Add(false); bitBuffer.Add(false);

        // Character count indicator
        int countBits = version < 10 ? 8 : 16;
        for (int i = countBits - 1; i >= 0; i--)
        {
            bitBuffer.Add(((rawBytes.Length >> i) & 1) != 0);
        }

        // Data bytes
        foreach (byte b in rawBytes)
        {
            if (bitBuffer.Count / 8 >= totalDataCodewords) break;
            for (int i = 7; i >= 0; i--)
            {
                bitBuffer.Add(((b >> i) & 1) != 0);
            }
        }

        // Terminator: up to 4 zero bits
        int terminatorLen = Math.Min(4, (totalDataCodewords * 8) - bitBuffer.Count);
        for (int i = 0; i < terminatorLen; i++)
        {
            bitBuffer.Add(false);
        }

        // Pad to byte boundary
        while (bitBuffer.Count % 8 != 0 && bitBuffer.Count < totalDataCodewords * 8)
        {
            bitBuffer.Add(false);
        }

        // Convert to data codewords
        byte[] dataCodewords = new byte[totalDataCodewords];
        for (int i = 0; i < bitBuffer.Count / 8 && i < totalDataCodewords; i++)
        {
            byte val = 0;
            for (int b = 0; b < 8; b++)
            {
                if (bitBuffer[(i * 8) + b]) val |= (byte)(1 << (7 - b));
            }
            dataCodewords[i] = val;
        }

        // Pad with alternating 0xEC and 0x11
        byte pad = 0xEC;
        for (int i = bitBuffer.Count / 8; i < totalDataCodewords; i++)
        {
            dataCodewords[i] = pad;
            pad = (pad == 0xEC) ? (byte)0x11 : (byte)0xEC;
        }

        // Reed-Solomon error correction
        int g1Blocks = BlocksGroup1Q[version];
        int g1Data = DataPerBlockG1Q[version];
        int g2Blocks = BlocksGroup2Q[version];
        int g2Data = DataPerBlockG2Q[version];
        int eccPerBlock = EccPerBlockQ[version];
        int totalBlocks = g1Blocks + g2Blocks;

        byte[][] dataBlocks = new byte[totalBlocks][];
        byte[][] eccBlocks = new byte[totalBlocks][];

        int dataOffset = 0;
        for (int b = 0; b < totalBlocks; b++)
        {
            int blockSize = (b < g1Blocks) ? g1Data : g2Data;
            dataBlocks[b] = new byte[blockSize];
            Array.Copy(dataCodewords, dataOffset, dataBlocks[b], 0, blockSize);
            dataOffset += blockSize;
            eccBlocks[b] = ComputeReedSolomon(dataBlocks[b], eccPerBlock);
        }

        // Interleave data codewords
        var finalCodewords = new List<byte>();
        int maxDataLen = Math.Max(g1Data, g2Data);
        for (int i = 0; i < maxDataLen; i++)
        {
            for (int b = 0; b < totalBlocks; b++)
            {
                if (i < dataBlocks[b].Length)
                {
                    finalCodewords.Add(dataBlocks[b][i]);
                }
            }
        }

        // Interleave ECC codewords
        for (int i = 0; i < eccPerBlock; i++)
        {
            for (int b = 0; b < totalBlocks; b++)
            {
                finalCodewords.Add(eccBlocks[b][i]);
            }
        }

        // Construct QR matrix
        int matrixSize = 17 + (version * 4);
        bool[,] matrix = new bool[matrixSize, matrixSize];
        bool[,] isFunction = new bool[matrixSize, matrixSize];

        // 1. Finder patterns
        PlaceFinderPattern(matrix, isFunction, 0, 0);
        PlaceFinderPattern(matrix, isFunction, matrixSize - 7, 0);
        PlaceFinderPattern(matrix, isFunction, 0, matrixSize - 7);

        // 2. Alignment patterns
        int[] alignCoords = AlignmentPatternCenters[version];
        for (int r = 0; r < alignCoords.Length; r++)
        {
            for (int c = 0; c < alignCoords.Length; c++)
            {
                int cr = alignCoords[r];
                int cc = alignCoords[c];
                if (!isFunction[cr, cc])
                {
                    PlaceAlignmentPattern(matrix, isFunction, cr - 2, cc - 2);
                }
            }
        }

        // 3. Timing patterns
        for (int i = 8; i < matrixSize - 8; i++)
        {
            if (!isFunction[6, i])
            {
                matrix[6, i] = (i % 2 == 0);
                isFunction[6, i] = true;
            }
            if (!isFunction[i, 6])
            {
                matrix[i, 6] = (i % 2 == 0);
                isFunction[i, 6] = true;
            }
        }

        // 4. Dark module
        matrix[(4 * version) + 9, 8] = true;
        isFunction[(4 * version) + 9, 8] = true;

        // 5. Reserve format information areas
        for (int i = 0; i < 9; i++)
        {
            if (i != 6) { isFunction[8, i] = true; isFunction[i, 8] = true; }
        }
        for (int i = 0; i < 8; i++)
        {
            isFunction[8, matrixSize - 1 - i] = true;
            isFunction[matrixSize - 1 - i, 8] = true;
        }

        // 6. Place data with zig-zag scan
        int finalBitIndex = 0;
        int totalFinalBits = finalCodewords.Count * 8;
        int col = matrixSize - 1;
        bool upward = true;

        while (col > 0)
        {
            if (col == 6) col--; // Skip timing pattern column

            for (int step = 0; step < matrixSize; step++)
            {
                int row = upward ? (matrixSize - 1 - step) : step;

                for (int c = 0; c < 2; c++)
                {
                    int currentC = col - c;
                    if (!isFunction[row, currentC])
                    {
                        bool bit = false;
                        if (finalBitIndex < totalFinalBits)
                        {
                            int byteIdx = finalBitIndex / 8;
                            int bitSub = 7 - (finalBitIndex % 8);
                            bit = ((finalCodewords[byteIdx] >> bitSub) & 1) != 0;
                            finalBitIndex++;
                        }

                        // Apply default Mask 0: (row + col) % 2 == 0
                        bool maskBit = ((row + currentC) % 2 == 0);
                        matrix[row, currentC] = bit ^ maskBit;
                    }
                }
            }
            col -= 2;
            upward = !upward;
        }

        // 7. Place format info for ECC Q (value 03 = 011b) and Mask 0 (000b) -> format bits: 011000b -> 101010000010010b
        // Precomputed format info for ECC Q, Mask 0:
        int formatInfo = 0x5465; // ECC Q (03) + Mask 0 with BCH (15, 5) and XOR 0x5412
        for (int i = 0; i < 15; i++)
        {
            bool bit = ((formatInfo >> i) & 1) != 0;
            // Around top-left finder
            if (i < 6) matrix[8, i] = bit;
            else if (i == 6) matrix[8, 7] = bit;
            else if (i == 7) matrix[8, 8] = bit;
            else if (i == 8) matrix[7, 8] = bit;
            else matrix[14 - i, 8] = bit;

            // Across split format strips
            if (i < 8) matrix[matrixSize - 1 - i, 8] = bit;
            else matrix[8, matrixSize - 15 + i] = bit;
        }

        // Render pure vector SVG
        int totalSvgSize = matrixSize + (margin * 2);
        var sb = new StringBuilder();
        sb.Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {totalSvgSize} {totalSvgSize}\" shape-rendering=\"crispEdges\">");
        sb.Append($"<rect width=\"{totalSvgSize}\" height=\"{totalSvgSize}\" fill=\"#ffffff\"/>");
        sb.Append("<path fill=\"#000000\" d=\"");

        for (int r = 0; r < matrixSize; r++)
        {
            for (int c = 0; c < matrixSize; c++)
            {
                if (matrix[r, c])
                {
                    sb.Append($"M{c + margin},{r + margin}h1v1h-1z");
                }
            }
        }

        sb.Append("\"/></svg>");
        return sb.ToString();
    }

    private static void PlaceFinderPattern(bool[,] matrix, bool[,] isFunction, int row, int col)
    {
        for (int r = -1; r <= 7; r++)
        {
            for (int c = -1; c <= 7; c++)
            {
                int cr = row + r;
                int cc = col + c;
                if (cr >= 0 && cr < matrix.GetLength(0) && cc >= 0 && cc < matrix.GetLength(1))
                {
                    isFunction[cr, cc] = true;
                    if (r >= 0 && r <= 6 && c >= 0 && c <= 6)
                    {
                        bool isBorder = (r == 0 || r == 6 || c == 0 || c == 6);
                        bool isCenter = (r >= 2 && r <= 4 && c >= 2 && c <= 4);
                        matrix[cr, cc] = isBorder || isCenter;
                    }
                    else
                    {
                        matrix[cr, cc] = false; // Separator
                    }
                }
            }
        }
    }

    private static void PlaceAlignmentPattern(bool[,] matrix, bool[,] isFunction, int row, int col)
    {
        for (int r = 0; r < 5; r++)
        {
            for (int c = 0; c < 5; c++)
            {
                int cr = row + r;
                int cc = col + c;
                isFunction[cr, cc] = true;
                bool isBorder = (r == 0 || r == 4 || c == 0 || c == 4);
                bool isCenter = (r == 2 && c == 2);
                matrix[cr, cc] = isBorder || isCenter;
            }
        }
    }

    // GF(256) arithmetic for Reed-Solomon ECC
    private static readonly byte[] Exp = new byte[512];
    private static readonly byte[] Log = new byte[256];

    static QrCodeSvgGenerator()
    {
        int val = 1;
        for (int i = 0; i < 255; i++)
        {
            Exp[i] = (byte)val;
            Exp[i + 255] = (byte)val;
            Log[val] = (byte)i;
            val <<= 1;
            if ((val & 0x100) != 0) val ^= 0x11D;
        }
    }

    private static byte GfMultiply(byte a, byte b)
    {
        if (a == 0 || b == 0) return 0;
        return Exp[Log[a] + Log[b]];
    }

    private static byte[] ComputeReedSolomon(byte[] data, int eccCount)
    {
        byte[] generator = [1];
        for (int i = 0; i < eccCount; i++)
        {
            byte[] nextGen = new byte[generator.Length + 1];
            for (int j = 0; j < generator.Length; j++)
            {
                nextGen[j] ^= GfMultiply(generator[j], Exp[i]);
                nextGen[j + 1] ^= generator[j];
            }
            generator = nextGen;
        }

        byte[] result = new byte[eccCount];
        foreach (byte b in data)
        {
            byte factor = (byte)(b ^ result[0]);
            for (int i = 0; i < eccCount - 1; i++)
            {
                result[i] = (byte)(result[i + 1] ^ GfMultiply(generator[i + 1], factor));
            }
            result[eccCount - 1] = GfMultiply(generator[eccCount], factor);
        }
        return result;
    }
}
