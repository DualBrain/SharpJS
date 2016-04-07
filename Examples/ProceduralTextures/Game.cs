using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Audio;
using Microsoft.Xna.Framework.Content;
using Microsoft.Xna.Framework.GamerServices;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Microsoft.Xna.Framework.Media;
using JSIL.Meta;

namespace ProceduralTextures {
    public class Game : Microsoft.Xna.Framework.Game {
        readonly GraphicsDeviceManager Graphics;
        SpriteBatch SpriteBatch;

        [JSPackedArray]
        public Color[] GradientPixels;
        public Texture2D GradientTexture1, GradientTexture2;

        public Game () {
            Graphics = new GraphicsDeviceManager(this);
            Graphics.PreferredBackBufferWidth = 1024;
            Graphics.PreferredBackBufferHeight = 768;

            IsFixedTimeStep = false;

            Content.RootDirectory = "Content";
        }

        /// <summary>
        /// LoadContent will be called once per game and is the place to load
        /// all of your content.
        /// </summary>
        protected override void LoadContent () {
            SpriteBatch = new SpriteBatch(GraphicsDevice);

            GradientTexture1 = new Texture2D(GraphicsDevice, 256, 256, false, SurfaceFormat.Color);
            GradientTexture2 = new Texture2D(GraphicsDevice, 256, 256, false, SurfaceFormat.Color);
            GradientPixels = new Color[GradientTexture1.Width * GradientTexture1.Height];
        }

        protected override void Update (GameTime gameTime) {
            GradientTexture1.GetData(GradientPixels);
            GradientTexture2.SetData(GradientPixels);

            var seconds = gameTime.TotalGameTime.TotalSeconds;

            var color1 = new Color(
                (float)Math.Sin(seconds % Math.PI), 
                (float)Math.Sin((seconds * 2.33) % Math.PI),
                (float)Math.Sin((seconds * 4.75) % Math.PI), 
                1f
            );
            var color2 = Color.Black;

            for (int y = 0, w = GradientTexture1.Width, h = GradientTexture1.Height; y < h; y++) {
                var rowOffset = y * w;
                var rowColor = Color.Lerp(color1, color2, (y / (float)h));

                for (int x = 0; x < w; x++)
                    GradientPixels[rowOffset + x] = rowColor;
            }

            GradientTexture1.SetData(GradientPixels);

            base.Update(gameTime);
        }

        protected override void Draw (GameTime gameTime) {
            GraphicsDevice.Clear(Color.CornflowerBlue);

            SpriteBatch.Begin();
            SpriteBatch.Draw(GradientTexture1, new Vector2(4, 4), Color.White);
            SpriteBatch.Draw(GradientTexture2, new Vector2(256 + 8, 4), Color.White);
            SpriteBatch.End();

            GraphicsDevice.Textures[0] = null;

            base.Draw(gameTime);
        }
    }
}
