using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Game.Graphics.Containers;
using osu.Game.Screens;

namespace osu_replay_renderer_netcore
{
    partial class RecorderScreenStack : OsuScreenStack
    {
        public float Parallax {
            get { return (InternalChildren[0] as ParallaxContainer).ParallaxAmount; }
            set { (InternalChildren[0] as ParallaxContainer).ParallaxAmount = value; }
        }

        public RecorderScreenStack() : base()
        {}

        protected override IReadOnlyDependencyContainer CreateChildDependencies(IReadOnlyDependencyContainer parent)
        {
            var dependencies = new DependencyContainer(base.CreateChildDependencies(parent));
            dependencies.CacheAs<BackgroundScreenStack>((InternalChildren[0] as ParallaxContainer).Children[0] as BackgroundScreenStack);
            return dependencies;
        }

        protected override void LoadComplete() { Parallax = 0.0f; }
    }
}
