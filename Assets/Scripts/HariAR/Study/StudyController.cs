// StudyController.cs
// ---------------------------------------------------------------------------
// The within-subjects evaluation harness (paper §4.1).
//
// The study compares two conditions on the same four tasks:
//
//   Baseline  GPS-arrow-only. A compass arrow to the destination and a raw
//             distance readout. No landmark names, no AR pathway, no spoken
//             landmark references.
//   HariAr    The full system: AR pathway, waypoint chevrons, landmark labels,
//             landmark-anchored spoken instructions.
//
// Both conditions use the same backend route, so the *only* variable is the
// guidance presentation — which is what makes the 21/24 vs 14/24 comparison
// mean anything. If the baseline used a different route or a different router,
// the comparison would be confounded and the Fisher's exact test invalid.
//
// Condition order is counterbalanced by participant id parity, as §4.1 states.
// ---------------------------------------------------------------------------

using System;
using UnityEngine;
using HariAR.Navigation;
using HariAR.AR;
using HariAR.UI;

namespace HariAR.Study
{
    public enum StudyCondition
    {
        /// <summary>GPS-arrow-only control condition.</summary>
        Baseline,
        /// <summary>Full landmark-anchored AR guidance.</summary>
        HariAr,
    }

    public class StudyController : MonoBehaviour
    {
        [Header("Participant")]
        public int participantId = 1;

        [Tooltip("Task index 1-4, per the paper's four navigation tasks.")]
        public int taskIndex = 1;

        [Header("Condition")]
        public StudyCondition condition = StudyCondition.HariAr;

        [Tooltip("Enable the study harness. Leave off for normal pilgrim use.")]
        public bool studyModeEnabled = false;

        [Header("Wiring")]
        public NavigationController nav;
        public ArContentManager content;
        public RouteRenderer routeRenderer;
        public ArrowController arrowController;
        public InstructionHUD hud;
        public StudyLogger logger;

        /// <summary>
        /// The four tasks of §4.1, all starting from GNC Tollgate:
        /// a service facility, a religious structure, a transport stop, and a
        /// multi-target sequence.
        /// </summary>
        public static readonly (string label, string query)[] Tasks =
        {
            ("service facility",  "Take me to the Ayurvedic Hospital"),
            ("religious structure", "Take me to the main temple"),
            ("transport stop",    "Take me to the APSRTC bus station"),
            ("multi-target",      "Take me to Ladoo Counter and then Anna Prasadam"),
        };

        void Start()
        {
            if (!studyModeEnabled) return;
            ApplyCondition(condition);
            logger?.BeginSession(participantId, condition, taskIndex);
        }

        /// <summary>
        /// Counterbalanced order, per §4.1: odd participants see the baseline
        /// first, even participants see HARI-AR first.
        /// </summary>
        public StudyCondition FirstCondition =>
            participantId % 2 == 1 ? StudyCondition.Baseline : StudyCondition.HariAr;

        public void ApplyCondition(StudyCondition c)
        {
            condition = c;
            bool full = c == StudyCondition.HariAr;

            // The AR pathway, chevrons and landmark labels are the manipulation.
            if (content != null) content.SetContentVisible(full);
            if (routeRenderer != null) routeRenderer.enabled = full;
            if (arrowController != null) arrowController.gameObject.SetActive(full);

            if (hud != null)
            {
                // The arrow is present in BOTH conditions — it is the baseline's
                // entire guidance, and removing it from HARI-AR would confound
                // the comparison in the opposite direction.
                hud.speakInstructions = full;
            }

            Debug.Log($"[HARI-AR][Study] P{participantId} task {taskIndex} → {c}");
            logger?.LogEvent("condition_applied", c.ToString());
        }

        /// <summary>Run the configured task's query.</summary>
        public void StartTask()
        {
            if (taskIndex < 1 || taskIndex > Tasks.Length)
            {
                Debug.LogError($"[HARI-AR][Study] Invalid task index {taskIndex}");
                return;
            }

            var (label, query) = Tasks[taskIndex - 1];
            logger?.LogEvent("task_started", $"{label}|{query}");
            nav?.Navigate(query);
        }

        /// <summary>
        /// Record a wayfinding error. §4.4 reports these per session
        /// (1.8 baseline vs 0.6 HARI-AR), so the observer needs a one-tap way
        /// to mark a wrong turn as it happens.
        /// </summary>
        public void MarkWayfindingError(string note = "")
        {
            logger?.LogWayfindingError(note);
        }

        /// <summary>Mark the outcome at a decision point — the RQ2 measure.</summary>
        public void MarkTurnDecision(string junction, bool correct)
        {
            logger?.LogEvent("turn_decision",
                             $"{junction}|{(correct ? "correct" : "incorrect")}");
        }

        public void CompleteTask(bool success)
        {
            logger?.LogEvent("task_completed", success ? "success" : "abandoned");
            logger?.EndSession();
        }

        /// <summary>Advance to the second condition of the counterbalanced pair.</summary>
        public void SwitchCondition()
        {
            var next = condition == StudyCondition.Baseline
                ? StudyCondition.HariAr
                : StudyCondition.Baseline;

            logger?.EndSession();
            ApplyCondition(next);
            logger?.BeginSession(participantId, next, taskIndex);
        }
    }
}
