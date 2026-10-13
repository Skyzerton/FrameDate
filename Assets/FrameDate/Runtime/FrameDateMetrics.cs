using System.Collections.Generic;
using UnityEngine;

namespace FrameDate
{
    /// <summary>
    /// Метрики боя реальной игры (SF4: Arena), к которым привязаны все длины FrameDate.
    /// Длины в приёмах хранятся и вводятся в Units — тех же, что в триггерах игры (1 Unit = 1 см);
    /// FrameDate рисует сцену в метрах, переводя через unitsPerMeter.
    /// Значения по умолчанию сняты 01.10.2026 с кода игры и замера на тренировке
    /// (арена bamboo_arena, Сержант против Ликвидатора).
    /// </summary>
    [CreateAssetMenu(fileName = "FrameDateMetrics", menuName = "FrameDate/Metrics")]
    public class FrameDateMetrics : ScriptableObject
    {
        public const string DefaultAssetPath = "Assets/FrameDate/Config/FrameDateMetrics.asset";

        [Header("Единицы")]
        [Tooltip("Сколько Units игры в одном метре сцены FrameDate. В игре 1 Unit = 1 единица мира = 1 см.")]
        public float unitsPerMeter = 100f;

        [Header("Арена по умолчанию")]
        [Tooltip("X левой стены: в неё упирается центр персонажа. bamboo_arena: −710. Арены игры — от 1386 до 2670 Units шириной.")]
        public float leftWallX = -710f;

        [Tooltip("X правой стены: в неё упирается центр персонажа. bamboo_arena: 675.7.")]
        public float rightWallX = 675.7f;

        [Header("Старт раунда")]
        [Tooltip("Дистанция между центрами на старте раунда. Замер на тренировке: 279 (константа кода игры 180 в этом режиме не применяется).")]
        public float startDistance = 279f;

        [Header("Тела")]
        [Tooltip("Ширина пушбокса в Idle — по умолчанию для обоих акторов, если у актора нет своей. Замер: Сержант 67, Ликвидатор 52.")]
        public float idleBodyWidth = 60f;

        [Tooltip("Масса стоящего персонажа при взаимном расталкивании. В игре стоя 1, лёжа 5.")]
        public float standingMass = 1f;

        [Header("Камера")]
        [Tooltip("Порог MaxDistanceBetweenModels: с этой дистанции между центрами включаются камерные границы. По умолчанию в игре 500, триггер SetMaxDistanceBetweenModels меняет его (на тренировке было 420).")]
        public float maxDistanceThreshold = 500f;

        [Tooltip("Камерные границы = середина между центрами ± доля × порог. В игре 0.55, поэтому предел дистанции = 1.1 × порог.")]
        public float cameraBorderRatio = 0.55f;

        public float ArenaWidth => rightWallX - leftWallX;

        /// <summary>Наибольшая дистанция между центрами, которую допускают камерные границы.</summary>
        public float MaxCenterDistance => 2f * cameraBorderRatio * maxDistanceThreshold;

        private static FrameDateMetrics _fallback;

        /// <summary>Встроенные значения для приёма без назначенного конфига. Не сохраняется.</summary>
        public static FrameDateMetrics Fallback
        {
            get
            {
                if (_fallback == null)
                {
                    _fallback = CreateInstance<FrameDateMetrics>();
                    _fallback.name = "FrameDateMetrics (встроенные)";
                    _fallback.hideFlags = HideFlags.HideAndDontSave;
                }
                return _fallback;
            }
        }

        public List<string> Validate()
        {
            var problems = new List<string>();
            if (!(unitsPerMeter > 0f))
                problems.Add($"Метрики: Units на метр {unitsPerMeter} должно быть больше нуля.");
            if (!(rightWallX > leftWallX))
                problems.Add($"Метрики: правая стена {rightWallX} должна быть правее левой {leftWallX}.");
            if (!(startDistance > 0f))
                problems.Add($"Метрики: стартовая дистанция {startDistance} должна быть больше нуля.");
            if (!(idleBodyWidth > 0f))
                problems.Add($"Метрики: ширина тела в Idle {idleBodyWidth} должна быть больше нуля.");
            if (!(standingMass > 0f))
                problems.Add($"Метрики: масса {standingMass} должна быть больше нуля.");
            if (!(maxDistanceThreshold > 0f))
                problems.Add($"Метрики: порог дистанции {maxDistanceThreshold} должен быть больше нуля.");
            if (!(cameraBorderRatio > 0f))
                problems.Add($"Метрики: доля камерной границы {cameraBorderRatio} должна быть больше нуля.");
            return problems;
        }
    }
}
