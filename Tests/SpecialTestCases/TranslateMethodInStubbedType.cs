﻿using System;
using JSIL.Meta;

public static class Program {
    public static void Main (string[] args) {
    }
}

[JSStubOnly]
public static class T {
    public static void ShouldBeExternal () {
        Console.WriteLine("This shouldn't be translated!");
    }

    [JSNeverStub]
    public static void ShouldNotBeExternal () {
        Console.WriteLine("This should be translated!");
    }
}