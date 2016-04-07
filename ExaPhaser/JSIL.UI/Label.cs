﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using JSIL.Dom;

namespace JSIL.UI
{
    public class Label: Element
    {
        public Label(string text): base("p")
        {
            TextContent = text;
        }
    }
}
