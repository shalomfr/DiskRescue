using System.Collections.Generic;

namespace DiskRescue.Core.Ntfs
{
    /// <summary>One extent of a non-resident attribute: Length clusters starting at Lcn (or sparse).</summary>
    public struct Run
    {
        public long Vcn;     // first virtual cluster of this run
        public long Length;  // length in clusters
        public long Lcn;     // starting logical cluster (-1 if sparse)
        public bool Sparse;
    }

    /// <summary>Decodes NTFS data-run lists (the variable-length cluster maps used by non-resident attributes).</summary>
    public static class DataRuns
    {
        public static List<Run> Parse(byte[] buf, int start)
        {
            var runs = new List<Run>();
            long lcn = 0, vcn = 0;
            int i = start;
            while (i < buf.Length)
            {
                byte header = buf[i++];
                if (header == 0) break;
                int lenBytes = header & 0x0F;
                int offBytes = (header >> 4) & 0x0F;
                if (lenBytes == 0 || i + lenBytes > buf.Length) break;

                long length = ReadVar(buf, i, lenBytes, signed: false); i += lenBytes;
                var run = new Run { Vcn = vcn, Length = length };

                if (offBytes == 0)
                {
                    run.Sparse = true; run.Lcn = -1;
                }
                else
                {
                    if (i + offBytes > buf.Length) break;
                    long delta = ReadVar(buf, i, offBytes, signed: true); i += offBytes;
                    lcn += delta; run.Lcn = lcn;
                }
                runs.Add(run);
                vcn += length;
            }
            return runs;
        }

        public static long MapVcnToLcn(List<Run> runs, long vcn, out bool sparse)
        {
            sparse = false;
            foreach (var r in runs)
                if (vcn >= r.Vcn && vcn < r.Vcn + r.Length)
                {
                    if (r.Sparse) { sparse = true; return -1; }
                    return r.Lcn + (vcn - r.Vcn);
                }
            return -1;
        }

        private static long ReadVar(byte[] b, int off, int n, bool signed)
        {
            long v = 0;
            for (int k = 0; k < n; k++) v |= (long)b[off + k] << (8 * k);
            if (signed && n > 0 && n < 8 && (b[off + n - 1] & 0x80) != 0)
                v |= -1L << (8 * n);   // sign-extend
            return v;
        }
    }
}
