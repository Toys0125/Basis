#include <math.h>
#include <stdint.h>
#include <stdio.h>
#include "numerel.h"

/*
 * Native comparison harness for cnlohr/numerel revision
 * 8676848ae268f3a8eee672413f272ee422521d09.
 *
 * Build example:
 *   cc -std=c11 -O2 numerel_oracle.c -I/path/to/numerel -lm -o numerel_oracle
 */

static void print_compress(void)
{
    const int values[] = { -1000, -729, -512, -343, -216, -125, -64, -27, -8, -1, 0,
                           1, 7, 8, 9, 26, 27, 28, 63, 64, 65, 124, 125, 126,
                           215, 216, 217, 342, 343, 344, 511, 512, 513, 728, 729, 730, 999, 1000 };
    const unsigned count = sizeof(values) / sizeof(values[0]);
    for (unsigned i = 0; i < count; ++i)
        printf("COMP\t%d\t%d\n", values[i], NumerelDiffCompress(values[i]));
}

static void print_gray_scramble(void)
{
    const unsigned widths[] = { 5, 8, 12, 16 };
    for (unsigned wi = 0; wi < sizeof(widths) / sizeof(widths[0]); ++wi)
    {
        unsigned bits = widths[wi];
        printf("SCRAMBLE\t%u", bits);
        for (unsigned frame = 0; frame < bits; ++frame)
            printf("\t%u", NumerelGrayScramble(frame, bits));
        printf("\n");
    }
}

static void print_sequence(unsigned numbits, unsigned looping)
{
    const unsigned values[] = { 2048, 2056, 2091, 2000, 4095, 0, 1234, 1234, 3000, 2048, 17, 4080 };
    numerel_tx tx = { 2048 };
    numerel_rx rx = { 2048, 2048, 0 };
    const unsigned count = sizeof(values) / sizeof(values[0]);

    for (unsigned frame = 0; frame < count; ++frame)
    {
        unsigned graybit = NumerelGrayScramble(frame % numbits, numbits);
        unsigned before = tx.remote_estimate;
        numeral_xfer xfer = NumerelEncode(&tx, values[frame], graybit, numbits, looping);
        unsigned consumed = NumerelDecode(&rx, xfer.bits, graybit, numbits, looping);
        printf("SEQ\t%u\t%u\t%u\t%u\t%u\t%u\t%08x\t%u\t%u\t%u\t%d\t%u\n",
            looping, frame, graybit, values[frame], before, tx.remote_estimate,
            xfer.bits, xfer.length, consumed, rx.raw_estimate, rx.last_delta, rx.output_value);
    }
}

static void print_loss_sequence(void)
{
    const unsigned numbits = 12;
    const unsigned values[] = { 2048, 2100, 2200, 2300, 2400, 2500, 2600, 2700 };
    const unsigned dropped[] = { 0, 0, 1, 1, 0, 0, 1, 0 };
    numerel_tx tx = { 2048 };
    numerel_rx rx = { 2048, 2048, 0 };

    for (unsigned frame = 0; frame < 8; ++frame)
    {
        unsigned graybit = NumerelGrayScramble(frame % numbits, numbits);
        numeral_xfer xfer = NumerelEncode(&tx, values[frame], graybit, numbits, 0);
        if (dropped[frame])
            NumerelApplyDelta(&rx, numbits, 0);
        else
            NumerelDecode(&rx, xfer.bits, graybit, numbits, 0);
        printf("LOSS\t%u\t%u\t%u\t%u\t%u\t%u\t%d\t%u\n",
            frame, dropped[frame], graybit, values[frame], tx.remote_estimate,
            rx.raw_estimate, rx.last_delta, rx.output_value);
    }
}

static void print_exhaustive_checksum(void)
{
    uint64_t hash = UINT64_C(14695981039346656037);
    for (int diff = -4095; diff <= 4095; ++diff)
    {
        int reconstructed = NumerelDiffDecompress(NumerelDiffCompress(diff));
        uint32_t value = (uint32_t)reconstructed;
        for (unsigned byte = 0; byte < 4; ++byte)
        {
            hash ^= (value >> (byte * 8)) & 0xffu;
            hash *= UINT64_C(1099511628211);
        }
    }
    printf("CHECKSUM\t12\t%016llx\n", (unsigned long long)hash);
}

int main(void)
{
    print_compress();
    print_gray_scramble();
    print_sequence(12, 0);
    print_sequence(12, 1);
    print_loss_sequence();
    print_exhaustive_checksum();
    return 0;
}
