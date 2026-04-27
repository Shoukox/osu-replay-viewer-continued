using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Screens;
using osu.Framework.Testing;
using osu.Game.Beatmaps;
using osu.Game.Rulesets.Difficulty;
using osu.Game.Rulesets.Judgements;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Objects;
using osu.Game.Rulesets.Objects.Drawables;
using osu.Game.Scoring;
using osu.Game.Screens.Play;
using osu.Game.Screens.Play.HUD;
using osu_replay_renderer_netcore.CustomHosts.CustomClocks;
using osu_replay_renderer_netcore.HUD.Builtin;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using osuTK;

namespace osu_replay_renderer_netcore
{
    partial class RecorderReplayPlayer : ReplayPlayer
    {
        public Score GivenScore { get; }
        public bool ManipulateClock { get; set; }
        public bool HideOverlays { get; }
        public Action OnFailed;

        private PerformanceGraph performanceGraph;
        private TrianglesPerformancePointsCounter rightSidePpCounter;
        private BeatmapDifficultyCache diffCache;
        private Bindable<int> ppCounter;
        private List<TimedDifficultyAttributes> timedAttrs;
        private Action<DrawableHitObject, JudgementResult> ppChangeHandler;

        public RecorderReplayPlayer(Score score, bool hideOverlays, bool skipIntro)
            : base(score, new PlayerConfiguration
            {
                AllowRestart = false,
                AllowPause = false,
                AllowUserInteraction = !hideOverlays,
                ShowLeaderboard = false,
                AllowSkipping = !hideOverlays,
                AutomaticallySkipIntro = skipIntro
            })
        {
            GivenScore = score;
            HideOverlays = hideOverlays;
        }

        protected override void PerformFail()
        {
            base.PerformFail();
            OnFailed?.Invoke();
            this.Push(CreateResults(GivenScore.ScoreInfo));
        }

        public override void OnSuspending(ScreenTransitionEvent e)
        {
            ValidForResume = false;
            base.OnSuspending(e);
        }

        protected override bool CheckModsAllowFailure()
        {
            return GameplayState.Mods
                .OfType<IApplicableFailOverride>()
                .All(m => m.PerformFail());
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();

            HUDOverlay.ShowHud.Value = false;
            HUDOverlay.HoldToQuit.Hide();

            if (HideOverlays)
            {
                ReplayOverlay.Hide();
                GameplayClockContainer.RemoveRecursive(v => v is SkipOverlay);
            }

            if (Game is OsuGameRecorder game)
            {
                if (game.ExperimentalFlags.Contains("pp-counter"))
                {
                    SetupRightSidePpCounter();
                }

                if (
                    game.ExperimentalFlags.Contains("performance-graph") ||
                    game.ExperimentalFlags.Contains("performance-points-graph") ||
                    game.ExperimentalFlags.Contains("pp-graph")
                )
                {
                    SetupPerformanceGraph();
                }
            }
        }

        private void SetupRightSidePpCounter()
        {
            if (rightSidePpCounter != null)
                return;

            AddInternal(rightSidePpCounter = new TrianglesPerformancePointsCounter
            {
                Anchor = Anchor.TopRight,
                Origin = Anchor.TopRight,
                Position = new Vector2(-20, 384),
            });

            ppCounter = rightSidePpCounter.Current;
        }

        private void SetupPerformanceGraph()
        {
            if (performanceGraph != null)
                return;

            AddInternal(performanceGraph = new PerformanceGraph
            {
                Anchor = Anchor.TopLeft,
                Origin = Anchor.TopLeft,
                Width = 300,
                Margin = new MarginPadding { Left = 10f, Top = 50f }
            });

            ppChangeHandler = (dho, judgement) =>
            {
                try
                {
                    if (Game is not OsuGameRecorder game)
                        return;

                    diffCache ??= Game.ChildrenOfType<BeatmapDifficultyCache>().FirstOrDefault();
                    if (diffCache == null)
                        return;

                    if (timedAttrs == null)
                    {
                        var task = diffCache.GetTimedDifficultyAttributesAsync(
                            game.WorkingBeatmap,
                            GameplayState.Ruleset,
                            Mods.Value.ToArray());

                        task.Wait();
                        timedAttrs = task.Result;
                    }

                    if (timedAttrs == null || timedAttrs.Count == 0)
                        return;

                    ppCounter ??= HUDOverlay.ChildrenOfType<PerformancePointsCounter>().FirstOrDefault()?.Current;

                    int attribIndex = timedAttrs.BinarySearch(
                        new TimedDifficultyAttributes(dho.HitObject.GetEndTime(), null));

                    if (attribIndex < 0)
                        attribIndex = ~attribIndex - 1;

                    attribIndex = Math.Clamp(attribIndex, 0, timedAttrs.Count - 1);

                    var attrib = timedAttrs[attribIndex].Attributes;
                    var calc = GameplayState.Ruleset.CreatePerformanceCalculator();

                    double totalPp = calc.Calculate(GameplayState.Score.ScoreInfo, attrib).Total;
                    performanceGraph.PP.Value = totalPp;

                    if (ppCounter != null)
                        ppCounter.Value = (int)Math.Round(totalPp);
                }
                catch
                {
                    // Avoid crashing replay rendering because of auxiliary PP graph logic.
                }
            };

            DrawableRuleset.Playfield.NewResult += ppChangeHandler;
        }

        protected override void StartGameplay()
        {
            if (!ManipulateClock)
            {
                base.StartGameplay();
                return;
            }

            GameplayClockContainer.Reset();
            GameplayClockContainer.Start();

            FieldInfo gameplayClockField = typeof(GameplayClockContainer)
                .GetField("GameplayClock", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

            if (gameplayClockField == null)
            {
                base.StartGameplay();
                return;
            }

            var ogClock = gameplayClockField.GetValue(GameplayClockContainer) as FramedBeatmapClock;
            var clock = ogClock?.Source as WrappedClock;

            if (clock != null)
            {
                foreach (Mod mod in GivenScore.ScoreInfo.Mods)
                {
                    if (mod is IApplicableToRate rateMod)
                        clock.RateMod = rateMod;
                }
            }

            if (Configuration.AutomaticallySkipIntro)
            {
                SchedulerAfterChildren.Add(() =>
                {
                    (GameplayClockContainer as MasterGameplayClockContainer)?.Skip();
                });
            }
        }

        protected override void Dispose(bool isDisposing)
        {
            if (ppChangeHandler != null && DrawableRuleset?.Playfield != null)
                DrawableRuleset.Playfield.NewResult -= ppChangeHandler;

            ppChangeHandler = null;
            timedAttrs = null;
            diffCache = null;
            ppCounter = null;
            performanceGraph = null;
            rightSidePpCounter = null;

            base.Dispose(isDisposing);
        }
    }
}
