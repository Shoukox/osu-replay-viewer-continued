using HarmonyLib;
using osu.Framework.Graphics.Rendering;
using System;
using System.Reflection;

namespace osu_replay_renderer_netcore.Patching
{
    public class RenderPatcher : PatcherBase
    {
        public override string PatcherId() => "osureplayrenderer.Render";

        public override void DoPatching()
        {
            base.DoPatching();

            PatchSwapBuffers("osu.Framework.Graphics.Veldrid.VeldridDevice", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            PatchSwapBuffers("osu.Framework.Graphics.OpenGL.GLRenderer", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        }

        private void PatchSwapBuffers(string typeName, BindingFlags flags)
        {
            var type = typeof(IRenderer).Assembly.GetType(typeName);
            if (type == null)
            {
                Console.Error.WriteLine($"[RenderPatcher] Skipping missing type: {typeName}");
                return;
            }

            var method = type.GetMethod("SwapBuffers", flags);
            if (method == null)
            {
                Console.Error.WriteLine($"[RenderPatcher] Skipping missing method: {typeName}.SwapBuffers");
                return;
            }

            Harmony.Patch(method, (Delegate)SwapBuffersPrefix);
        }

        public static event Action OnDraw;
        private static void TriggerOnDraw() => OnDraw?.Invoke();

        [HarmonyPatch(typeof(Renderer))]
        [HarmonyPatch("FinishFrame")]
        class PatchRendererFinishFrame
        {
            static void Prefix(Renderer __instance)
            {
                TriggerOnDraw();
            }
        }

        static bool SwapBuffersPrefix(object __instance)
        {
            return false;
        }
    }
}
