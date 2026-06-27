using System.Collections.Generic;

namespace DiskRescue.Core.Carving
{
    /// <summary>A file type recognizable by a magic header (and optionally a footer).</summary>
    public sealed class FileSignature
    {
        public string Ext;
        public byte[] Header;
        public byte[] Footer;   // null => carve up to MaxSize
        public int PreOffset;   // header appears this many bytes into the file (e.g. mp4 'ftyp' at +4)
        public long MaxSize;

        public FileSignature(string ext, byte[] header, byte[] footer, long maxSize, int preOffset = 0)
        { Ext = ext; Header = header; Footer = footer; MaxSize = maxSize; PreOffset = preOffset; }
    }

    public static class FileSignatures
    {
        public static readonly List<FileSignature> All = new List<FileSignature>
        {
            // images
            new FileSignature("jpg", new byte[]{0xFF,0xD8,0xFF}, new byte[]{0xFF,0xD9}, 30L*1024*1024),
            new FileSignature("png", new byte[]{0x89,0x50,0x4E,0x47,0x0D,0x0A,0x1A,0x0A},
                                     new byte[]{0x49,0x45,0x4E,0x44,0xAE,0x42,0x60,0x82}, 40L*1024*1024),
            new FileSignature("gif", new byte[]{0x47,0x49,0x46,0x38}, new byte[]{0x00,0x3B}, 20L*1024*1024),
            new FileSignature("bmp", new byte[]{0x42,0x4D}, null, 20L*1024*1024),
            // documents
            new FileSignature("pdf", new byte[]{0x25,0x50,0x44,0x46}, new byte[]{0x25,0x25,0x45,0x4F,0x46}, 48L*1024*1024),
            new FileSignature("zip", new byte[]{0x50,0x4B,0x03,0x04}, null, 48L*1024*1024), // also docx/xlsx/pptx
            new FileSignature("doc", new byte[]{0xD0,0xCF,0x11,0xE0,0xA1,0xB1,0x1A,0xE1}, null, 32L*1024*1024),
            // audio / video
            new FileSignature("mp4", new byte[]{0x66,0x74,0x79,0x70}, null, 256L*1024*1024, preOffset: 4),
            new FileSignature("mp3", new byte[]{0x49,0x44,0x33}, null, 20L*1024*1024),
            new FileSignature("wav", new byte[]{0x52,0x49,0x46,0x46}, null, 64L*1024*1024),
        };

        public static int MaxHeaderSpan
        {
            get { int m = 0; foreach (var s in All) { int span = s.PreOffset + s.Header.Length; if (span > m) m = span; } return m; }
        }
    }
}
