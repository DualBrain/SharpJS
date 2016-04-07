﻿using System;

#pragma warning disable 0169

public struct IntFloatPair {
    public int Int;
    public float Float;

    public override string ToString () {
        return String.Format("Int={0:0000}, Float={1:000.000}", Int, Float);
    }
}

public struct TwoIntFloatPairs {
    public IntFloatPair A, B;

    public override string ToString () {
        return String.Format("A=<{0}> B=<{1}>", A, B);
    }
}

public struct EmptyStruct {
}

public struct TwoBytes {
    byte a, b;
}

public struct TwoBytesOneInt {
    byte a, b;
    int c;
}

public struct TwoBytesShortDouble {
    byte a, b;
    short c;
    double d;
}

public struct DoubleTwoBytes {
    double a;
    byte b, c;
}

public struct ByteNestedByte {
    byte a;
    DoubleTwoBytes b;
    byte c;
}

public static class Util {
    public static void PrintBytes (byte[] bytes) {
        foreach (var b in bytes)
            Console.Write("{0:X2} ", b);

        Console.WriteLine();
    }
}

#pragma warning restore 0169