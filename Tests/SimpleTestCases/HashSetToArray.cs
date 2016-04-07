﻿using System;
using System.Collections.Generic;
using System.Linq;

public static class Program {
    public static void Main (string[] args) {
        var intHashSet = new HashSet<int>() { 1, 2, 3, 4 };

        Console.WriteLine(intHashSet.Count);

        int[] intArr = intHashSet.ToArray<int>();   // System.Linq.Enumerable::ToArray<int32>

        Console.WriteLine(intArr.Length);
        Console.WriteLine(intArr[0]);
        Console.WriteLine(intArr[1]);
        Console.WriteLine(intArr[2]);
        Console.WriteLine(intArr[3]);

        intArr = intHashSet.ToArray();   // System.Collections.Generic.HashSet`1<int32>::ToArray()

        Console.WriteLine(intArr.Length);
        Console.WriteLine(intArr[0]);
        Console.WriteLine(intArr[1]);
        Console.WriteLine(intArr[2]);
        Console.WriteLine(intArr[3]);
    }

}