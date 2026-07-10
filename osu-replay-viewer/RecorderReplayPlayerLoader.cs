using System;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Game.Graphics;
using osu.Game.Graphics.Sprites;
using osu.Game.Graphics.UserInterface;
using osu.Framework.Screens;
using osu.Game.Screens.Play;
using System.Linq;

namespace osu_replay_renderer_netcore
{
    internal partial class RecorderReplayPlayerLoader : PlayerLoader
    {
        private RecorderReplayPlayer player;
        private bool entered;

        public event Action OnEntered;

        public RecorderReplayPlayerLoader(RecorderReplayPlayer player) : base(() => player)
        {
            this.player = player;
        }

        public override void OnEntering(ScreenTransitionEvent e)
        {
            var backgroundProperty = typeof(osu.Game.Screens.OsuScreen).GetProperty("backgroundStack", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            backgroundProperty?.SetValue(this, Dependencies?.Get(typeof(osu.Game.Screens.BackgroundScreenStack)));
            base.OnEntering(e);
            entered = true;
            OnEntered?.Invoke();
        }

        protected override void Update()
        {
            // ScreenStack attaches a screen to the drawable tree before its
            // OnEntering() callback is dispatched. PlayerLoader.Update() calls
            // ApplyToBackground(), so allowing that first update to run would
            // crash before OsuScreen has registered its background. This can
            // be observed on Linux/SDL3 (the update ordering is timing-sensitive).
            if (!entered)
                return;

            base.Update();
        }

        protected override void LoadComplete()
        {
            LoadComponent(player);
            base.LoadComplete();
            PlayerSettings.RemoveAll(v => true, true);
            
            (MetadataInfo.Children[0] as FillFlowContainer).RemoveRecursive(v => v is LoadingLayer);
            var mapMetadata = (MetadataInfo.Children[0] as FillFlowContainer).Children[5] as GridContainer;
            mapMetadata.RowDimensions = new[]
            {
                new Dimension(GridSizeMode.AutoSize),
                new Dimension(GridSizeMode.AutoSize),
                new Dimension(GridSizeMode.AutoSize),
                new Dimension()
            };
            mapMetadata.Content = new[]
            {
                mapMetadata.Content[0].ToArray(),
                mapMetadata.Content[1].ToArray(),
                CreateNewRulesetMetadata("Played by", player.GivenScore.ScoreInfo.User.Username),
                CreateNewRulesetMetadata("Ruleset", player.GivenScore.ScoreInfo.Ruleset.Name)
            };
        }

        private Drawable[] CreateNewRulesetMetadata(string c1, string c2)
        {
            return new Drawable[]
            {
                new OsuSpriteText
                {
                    Anchor = Anchor.TopRight,
                    Origin = Anchor.TopRight,
                    Margin = new MarginPadding { Right = 5 },
                    Colour = OsuColour.Gray(0.8f),
                    Text = c1
                },
                new OsuSpriteText
                {
                    Margin = new MarginPadding { Left = 5 },
                    Text = c2
                }
            };
        }
    }
}
