using System;
using UnityEditor;
using UnityEngine;

namespace FrameDate.EditorTools
{
    /// <summary>
    /// Вьюпорт приёма. Рисует акторов на заданном кадре.
    ///
    /// Сознательно не создаёт объектов сцены: картинка — чистая функция от данных приёма
    /// и номера кадра. Поэтому вьюпорт не может испачкать открытую сцену, оставить мусор
    /// или записать что-либо в ассеты.
    /// </summary>
    public sealed class FrameDatePreview : IDisposable
    {
        private PreviewRenderUtility _utility;
        // Капсула собирается из цилиндра и двух сфер, чтобы картинка совпадала с расчётом контакта.
        // Встроенные меши здесь радиуса 1 и полувысоты 1 (проверено по вершинам), масштаб считается от этого.
        private Mesh _cylinderMesh;
        private Mesh _sphereMesh;
        private readonly System.Collections.Generic.List<Material> _hitMaterials = new System.Collections.Generic.List<Material>();
        private int _hitMaterialsUsed;
        private Material _attackerMaterial;
        private Material _targetMaterial;

        private static readonly Color NeutralColor = new Color(0.72f, 0.72f, 0.74f);
        private static readonly Color BackgroundColor = new Color(0.16f, 0.17f, 0.19f);

        private void EnsureInitialised()
        {
            if (_utility != null) return;

            _utility = new PreviewRenderUtility();

            var camera = _utility.camera;
            camera.orthographic = true;
            camera.orthographicSize = 1.9f;
            camera.nearClipPlane = 0.1f;
            camera.farClipPlane = 40f;
            camera.transform.position = new Vector3(0f, 0.1f, -10f);
            camera.transform.rotation = Quaternion.identity;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = BackgroundColor;

            _utility.ambientColor = new Color(0.32f, 0.32f, 0.35f);
            _utility.lights[0].intensity = 1.1f;
            _utility.lights[0].transform.rotation = Quaternion.Euler(35f, 35f, 0f);
            _utility.lights[1].intensity = 0.45f;
            _utility.lights[1].transform.rotation = Quaternion.Euler(-20f, -60f, 0f);

            _cylinderMesh = Resources.GetBuiltinResource<Mesh>("Cylinder.fbx");
            _sphereMesh = Resources.GetBuiltinResource<Mesh>("Sphere.fbx");

            var shader = Shader.Find("Standard");
            _attackerMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            _targetMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
        }

        /// <summary>
        /// Цвет актора на кадре: цвет первой активной дорожки этого актора,
        /// либо нейтральный, если активных нет. Порядок дорожек задаёт приоритет.
        /// </summary>
/// <summary>
        /// Цвет тела на кадре: цвет первой активной дорожки состояния этого актора,
        /// либо нейтральный. Attack и Movement цвет тела не задают: атаку показывает hitbox,
        /// движение видно по положению. Порядок дорожек задаёт приоритет.
        /// </summary>
        private static Color ResolveColor(FrameDateMove move, Actor actor, int frame)
        {
            for (int i = 0; i < move.tracks.Count; i++)
            {
                var track = move.tracks[i];
                if (track.actor != actor) continue;
                if (track.role == TrackRole.Attack || track.role == TrackRole.Movement) continue;
                if (track.IsActiveAt(frame)) return track.color;
            }
            return NeutralColor;
        }

public void Draw(Rect rect, FrameDateMove move, int frame)
        {
            // Сравнение "<" не ловит NaN: любое сравнение с NaN даёт false, поэтому
            // нечисловой размер до первой раскладки IMGUIContainer проходил охрану
            // и ронял BeginPreview на RenderTexture.Create. Проверяем от положительного.
            if (!(rect.width >= 4f) || !(rect.height >= 4f)) return;

            if (move == null)
            {
                EditorGUI.DrawRect(rect, BackgroundColor);
                var hint = new GUIStyle(EditorStyles.centeredGreyMiniLabel) { wordWrap = true };
                GUI.Label(rect, "Приём не выбран", hint);
                return;
            }

            EnsureInitialised();

            var sim = FrameDateSimulation.Run(move);
            var metrics = move.Metrics;
            // Данные — в Units игры, сцена — в метрах.
            float scale = 1f / Mathf.Max(1e-4f, metrics.unitsPerMeter);
            int f = sim.frameCount == 0 ? 0 : Mathf.Clamp(frame, 0, sim.frameCount - 1);
            var attacker = sim.CenterAt(move, Actor.Attacker, frame);

            // Камера следит за серединой между бойцами, как в игре; масштаб постоянный.
            float middleX = sim.frameCount == 0 ? 0f : (sim.attackerX[f] + sim.targetX[f]) * 0.5f;
            var camera = _utility.camera;
            camera.transform.position = new Vector3(middleX * scale, CameraHeight, -10f);

            _attackerMaterial.color = ResolveColor(move, Actor.Attacker, frame);
            _targetMaterial.color = ResolveColor(move, Actor.Target, frame);
            _hitMaterialsUsed = 0;

            _utility.BeginPreview(rect, GUIStyle.none);

            // С капсулами из игры тела рисуются ими, иначе — капсулами тела FrameDate.
            if (move.pose != null && move.pose.HasCapsules) DrawGameAttacker(move, sim, frame, scale);
            else DrawCapsule(sim.HurtboxAt(move, Actor.Attacker, frame), 0f, _attackerMaterial, scale);
            DrawGameTarget(move, sim, frame, scale);

            // Hitbox каждой дорожки Attack виден всегда и стоит перед телами.
            // Активный — цветом дорожки, на кадре контакта белый. Вне интервала — приглушённый,
            // в форме ближайшего следующего интервала, а после последнего — последнего.
            // Приглушённый hitbox в расчёте контакта не участвует. Пушбоксы не рисуются.
            for (int t = 0; t < move.tracks.Count; t++)
            {
                var track = move.tracks[t];
                if (track.role != TrackRole.Attack || track.actor != Actor.Attacker) continue;

                bool active;
                int s = PickShownSpan(track, frame, out active);
                if (s < 0) continue;
                // Удары с деталями из игры рисует DrawGameAttacker — капсулами самих деталей.
                if (FrameDateSimulation.UsesGameHitbox(move, track.spans[s])) continue;

                Color color;
                if (active)
                {
                    int c = sim.FindContact(t, s);
                    color = c >= 0 && sim.contacts[c].frame == frame ? Color.white : track.color;
                }
                else
                {
                    color = Color.Lerp(track.color, BackgroundColor, 0.62f);
                }

                DrawCapsule(FrameDateSimulation.Hitbox(track.spans[s], attacker), -0.6f, NextHitMaterial(color), scale);
            }

            _utility.camera.Render();
            var texture = _utility.EndPreview();
            GUI.DrawTexture(rect, texture, ScaleMode.StretchToFill, false);

            if (sim.frameCount > 0)
            {
                float pxPerUnit = rect.height / (2f * camera.orthographicSize) * scale;
                DrawArena(rect, move, sim, f, middleX, pxPerUnit);
            }

            DrawOverlay(rect, move, frame, sim);
        }

        private const float CameraHeight = 0.1f;

        /// <summary>Показывать импортированный скелет поверх капсул.</summary>
        public bool ShowSkeleton = true;

        private static readonly Color SkeletonColor = new Color(1f, 1f, 1f, 0.85f);

        /// <summary>
        /// Скелет атакующего линиями. Кости ставятся относительно motion-кости на тот же кадр и привязываются
        /// к рассчитанному X атакующего (с продвижением и расталкиванием), ступни — к низу капсулы тела.
        /// Персонаж в игре смотрит в −X, у нас атакующий смотрит в +X: отсюда forwardSign.
        /// </summary>
        private static void DrawSkeleton(FrameDateMove move, FrameDateSimulation sim, int f,
            System.Func<float, float> sx, System.Func<float, float> sy)
        {
            var pose = move.pose;
            int pf = Mathf.Clamp(f, 0, pose.frameCount - 1);
            var motion = pose.Position(pf, pose.motionBone);
            float groundY = move.attackerPosition.y - move.attackerBody.height * 0.5f;
            float baseX = sim.attackerX[f];

            var points = new Vector3[pose.boneCount];
            for (int i = 0; i < pose.boneCount; i++)
            {
                var p = pose.Position(pf, i);
                points[i] = new Vector3(sx(baseX + pose.forwardSign * (p.x - motion.x)), sy(groundY + p.y), 0f);
            }

            var old = Handles.color;
            Handles.color = SkeletonColor;
            for (int i = 0; i < pose.boneCount; i++)
            {
                int parent = pose.boneParents[i];
                // Якорь и armature стоят в начале координат анимации, а не на теле: их связи не рисуем.
                if (parent <= 1) continue;
                Handles.DrawAAPolyLine(2.5f, points[parent], points[i]);
            }
            Handles.color = old;
        }
        private static readonly Color WallColor = new Color(0.07f, 0.07f, 0.08f, 0.82f);
        private static readonly Color WallEdgeColor = new Color(0.62f, 0.64f, 0.70f, 0.9f);
        private static readonly Color CameraBorderColor = new Color(1f, 0.62f, 0.25f, 0.95f);

        /// <summary>
        /// Арена поверх картинки: пол с метровыми делениями, стены, включившиеся камерные границы
        /// и мини-карта всей арены. Мировой X в Units переводится в пиксели той же камерой, что рисует сцену.
        /// </summary>
        private void DrawArena(Rect rect, FrameDateMove move, FrameDateSimulation sim, int f, float middleX, float pxPerUnit)
        {
            var metrics = move.Metrics;
            System.Func<float, float> sx = x => rect.center.x + (x - middleX) * pxPerUnit;
            float cameraY = CameraHeight * metrics.unitsPerMeter;
            System.Func<float, float> sy = y => rect.center.y - (y - cameraY) * pxPerUnit;

            // Пол: низ тела атакующего на кадре 0, деления через 100 Units (1 м).
            float groundY = Mathf.Round(sy(move.attackerPosition.y - move.attackerBody.height * 0.5f));
            if (groundY > rect.y && groundY < rect.yMax)
            {
                EditorGUI.DrawRect(new Rect(rect.x, groundY, rect.width, 1f), new Color(1f, 1f, 1f, 0.18f));
                float viewLeft = middleX - (rect.width * 0.5f) / pxPerUnit;
                float viewRight = middleX + (rect.width * 0.5f) / pxPerUnit;
                for (float x = Mathf.Ceil(viewLeft / 100f) * 100f; x <= viewRight; x += 100f)
                {
                    float px = Mathf.Round(sx(x));
                    EditorGUI.DrawRect(new Rect(px, groundY, 1f, 5f), new Color(1f, 1f, 1f, 0.28f));
                    if (Mathf.Abs(x % 200f) < 0.5f)
                        GUI.Label(new Rect(px + 2f, groundY + 2f, 60f, 14f), x.ToString("0"), TickStyle);
                }
            }

            // Скелет из игры — поверх капсул, под стенами.
            if (ShowSkeleton && move.pose != null && move.pose.IsValid && Event.current.type == EventType.Repaint)
            {
                DrawSkeleton(move, sim, f, sx, sy);
                DrawTargetSkeleton(move, sim, f, sx, sy);
            }

            // Стены: всё, что за ними, затемнено.
            float leftWall = sx(metrics.leftWallX);
            if (leftWall > rect.x)
            {
                float w = Mathf.Min(leftWall, rect.xMax) - rect.x;
                EditorGUI.DrawRect(new Rect(rect.x, rect.y, w, rect.height), WallColor);
                if (leftWall < rect.xMax)
                {
                    EditorGUI.DrawRect(new Rect(leftWall - 2f, rect.y, 2f, rect.height), WallEdgeColor);
                    GUI.Label(new Rect(leftWall - 64f, rect.center.y - 8f, 60f, 16f), "стена", RightLabel);
                }
            }
            float rightWall = sx(metrics.rightWallX);
            if (rightWall < rect.xMax)
            {
                float x0 = Mathf.Max(rightWall, rect.x);
                EditorGUI.DrawRect(new Rect(x0, rect.y, rect.xMax - x0, rect.height), WallColor);
                if (rightWall > rect.x)
                {
                    EditorGUI.DrawRect(new Rect(rightWall, rect.y, 2f, rect.height), WallEdgeColor);
                    GUI.Label(new Rect(rightWall + 4f, rect.center.y - 8f, 60f, 16f), "стена", TickStyle);
                }
            }

            // Камерные границы — только когда включились и стоят внутри стен.
            if (sim.cameraEngaged[f])
            {
                if (sim.leftLimit[f] > metrics.leftWallX + 0.01f) DrawDashedLine(rect, sx(sim.leftLimit[f]), "камера", true);
                if (sim.rightLimit[f] < metrics.rightWallX - 0.01f) DrawDashedLine(rect, sx(sim.rightLimit[f]), "камера", false);
            }

            DrawMinimap(rect, move, sim, f, middleX, pxPerUnit);
        }

        private static readonly Color IdlePartColor = new Color(0.62f, 0.64f, 0.70f);

        /// <summary>
        /// Атакующий капсулами из игры: Collidable — цветом тела; атакующие детали (из AttackingParts ударов приёма)
        /// видны всегда — в активном интервале цветом дорожки, на кадре контакта белым, иначе приглушённо.
        /// Летящее оружие видно, потому что его капсулы привязаны к костям оружия.
        /// </summary>
        private void DrawGameAttacker(FrameDateMove move, FrameDateSimulation sim, int frame, float scale)
        {
            var pose = move.pose;
            int f = Mathf.Clamp(frame, 0, sim.frameCount - 1);
            float x = sim.attackerX[f];

            var parts = new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            var active = new System.Collections.Generic.Dictionary<string, Color>(System.StringComparer.OrdinalIgnoreCase);
            for (int t = 0; t < move.tracks.Count; t++)
            {
                var track = move.tracks[t];
                if (track.role != TrackRole.Attack || track.actor != Actor.Attacker) continue;
                for (int s = 0; s < track.spans.Count; s++)
                {
                    var span = track.spans[s];
                    if (span.attackingParts == null) continue;
                    int c = sim.FindContact(t, s);
                    bool contactNow = c >= 0 && sim.contacts[c].frame == f;
                    foreach (var part in span.attackingParts)
                    {
                        parts.Add(part);
                        if (span.Contains(frame)) active[part] = contactNow ? Color.white : track.color;
                    }
                }
            }

            for (int c = 0; c < pose.capsules.Count; c++)
            {
                var info = pose.capsules[c];
                bool attacking = parts.Contains(info.name);
                if (!info.collidable && !attacking) continue;
                var capsule = FrameDateSimulation.AttackerGameCapsule(move, frame, c, x);
                if (attacking)
                {
                    Color color;
                    if (!active.TryGetValue(info.name, out color)) color = Color.Lerp(IdlePartColor, BackgroundColor, 0.45f);
                    DrawCapsule(capsule, -0.6f, NextHitMaterial(color), scale);
                }
                else
                {
                    DrawCapsule(capsule, 0f, _attackerMaterial, scale);
                }
            }
        }

        /// <summary>Цель капсулами Idle героя из игры, повёрнутая к атакующему.</summary>
/// <summary>Цель: капсулами реакции, если она идёт; иначе капсулами Idle героя из игры или капсулой тела FrameDate.</summary>
        private void DrawGameTarget(FrameDateMove move, FrameDateSimulation sim, int frame, float scale)
        {
            int f = Mathf.Clamp(frame, 0, sim.frameCount - 1);
            FrameDateReactionCatalog.Reaction reaction;
            int local;
            FrameDateSimulation.ReactionPlay play;
            if (sim.ActiveReaction(move, f, out reaction, out local, out play))
            {
                var catalog = move.reactions;
                for (int c = 0; c < catalog.capsules.Count; c++)
                    if (catalog.capsules[c].collidable)
                        DrawCapsule(FrameDateSimulation.TargetReactionCapsule(move, reaction, local, c, sim.targetX[f]), 0f, _targetMaterial, scale);
                return;
            }

            var pose = move.pose;
            if (pose != null && pose.HasIdle)
            {
                for (int c = 0; c < pose.capsules.Count; c++)
                    if (pose.capsules[c].collidable)
                        DrawCapsule(FrameDateSimulation.TargetGameCapsule(move, c, sim.targetX[f]), 0f, _targetMaterial, scale);
                return;
            }

            DrawCapsule(sim.HurtboxAt(move, Actor.Target, frame), 0f, _targetMaterial, scale);
        }

/// <summary>Скелет цели: поза реакции, если она идёт, иначе Idle героя. Цель смотрит в −X.</summary>
        private static void DrawTargetSkeleton(FrameDateMove move, FrameDateSimulation sim, int f,
            System.Func<float, float> sx, System.Func<float, float> sy)
        {
            float groundY = FrameDateSimulation.GroundY(move, Actor.Target);
            float baseX = sim.targetX[f];
            Vector3[] points;
            int[] parents;

            FrameDateReactionCatalog.Reaction reaction;
            int local;
            FrameDateSimulation.ReactionPlay play;
            if (sim.ActiveReaction(move, f, out reaction, out local, out play))
            {
                var catalog = move.reactions;
                float motionX = catalog.Position(reaction, local, catalog.motionBone).x;
                parents = catalog.boneParents;
                points = new Vector3[catalog.boneCount];
                for (int i = 0; i < catalog.boneCount; i++)
                {
                    var p = catalog.Position(reaction, local, i);
                    points[i] = new Vector3(sx(baseX - catalog.forwardSign * (p.x - motionX)), sy(groundY + p.y), 0f);
                }
            }
            else
            {
                var pose = move.pose;
                if (pose == null || !pose.HasIdle || pose.idlePositions == null || pose.idlePositions.Length != pose.boneCount * 3) return;
                parents = pose.boneParents;
                points = new Vector3[pose.boneCount];
                for (int i = 0; i < pose.boneCount; i++)
                {
                    var p = pose.IdlePosition(i);
                    points[i] = new Vector3(sx(baseX - pose.forwardSign * (p.x - pose.idleMotionX)), sy(groundY + p.y), 0f);
                }
            }

            var old = Handles.color;
            Handles.color = new Color(1f, 1f, 1f, 0.45f);
            for (int i = 0; i < points.Length; i++)
            {
                int parent = parents[i];
                if (parent <= 1) continue;
                Handles.DrawAAPolyLine(2f, points[parent], points[i]);
            }
            Handles.color = old;
        }

        private static void DrawDashedLine(Rect rect, float x, string label, bool labelLeft)
        {
            if (x < rect.x || x > rect.xMax) return;
            for (float y = rect.y; y < rect.yMax; y += 10f)
                EditorGUI.DrawRect(new Rect(x - 1f, y, 2f, Mathf.Min(6f, rect.yMax - y)), CameraBorderColor);
            var style = new GUIStyle(labelLeft ? RightLabel : TickStyle) { normal = { textColor = CameraBorderColor } };
            GUI.Label(labelLeft ? new Rect(x - 64f, rect.center.y + 10f, 60f, 16f) : new Rect(x + 4f, rect.center.y + 10f, 60f, 16f), label, style);
        }

        /// <summary>Вся арена полоской внизу: стены по краям, бойцы, видимая часть и камерные границы.</summary>
        private void DrawMinimap(Rect rect, FrameDateMove move, FrameDateSimulation sim, int f, float middleX, float pxPerUnit)
        {
            var metrics = move.Metrics;
            var bar = new Rect(rect.x + 8f, rect.yMax - 34f, rect.width - 16f, 10f);
            float width = Mathf.Max(1f, metrics.ArenaWidth);
            System.Func<float, float> mx = x => bar.x + Mathf.Clamp01((x - metrics.leftWallX) / width) * bar.width;

            EditorGUI.DrawRect(bar, new Color(0f, 0f, 0f, 0.45f));
            EditorGUI.DrawRect(new Rect(bar.x, bar.y, 2f, bar.height), WallEdgeColor);
            EditorGUI.DrawRect(new Rect(bar.xMax - 2f, bar.y, 2f, bar.height), WallEdgeColor);

            float halfView = (rect.width * 0.5f) / pxPerUnit;
            float v0 = mx(middleX - halfView);
            float v1 = mx(middleX + halfView);
            EditorGUI.DrawRect(new Rect(v0, bar.y, Mathf.Max(1f, v1 - v0), bar.height), new Color(1f, 1f, 1f, 0.10f));

            if (sim.cameraEngaged[f])
            {
                EditorGUI.DrawRect(new Rect(mx(sim.leftLimit[f]) - 1f, bar.y - 2f, 2f, bar.height + 4f), CameraBorderColor);
                EditorGUI.DrawRect(new Rect(mx(sim.rightLimit[f]) - 1f, bar.y - 2f, 2f, bar.height + 4f), CameraBorderColor);
            }

            EditorGUI.DrawRect(new Rect(mx(sim.attackerX[f]) - 2f, bar.y + 1f, 4f, bar.height - 2f),
                sim.attackerAtLimit[f] ? Color.white : _attackerMaterial.color);
            EditorGUI.DrawRect(new Rect(mx(sim.targetX[f]) - 2f, bar.y + 1f, 4f, bar.height - 2f),
                sim.targetAtLimit[f] ? Color.white : _targetMaterial.color);
        }

        private static GUIStyle _tickStyle;
        private static GUIStyle TickStyle => _tickStyle ?? (_tickStyle = new GUIStyle(EditorStyles.miniLabel)
        {
            normal = { textColor = new Color(0.70f, 0.71f, 0.75f) }
        });

        private static GUIStyle _rightLabel;
        private static GUIStyle RightLabel => _rightLabel ?? (_rightLabel = new GUIStyle(TickStyle) { alignment = TextAnchor.UpperRight });

        /// <summary>
        /// Какой интервал дорожки Attack показать на кадре: активный; иначе ближайший следующий;
        /// иначе последний прошедший. −1 — у дорожки нет интервалов.
        /// </summary>
        private static int PickShownSpan(FrameTrack track, int frame, out bool active)
        {
            active = false;
            int next = -1;
            int previous = -1;
            for (int s = 0; s < track.spans.Count; s++)
            {
                var span = track.spans[s];
                if (span.Contains(frame))
                {
                    active = true;
                    return s;
                }
                if (span.startFrame > frame && (next < 0 || span.startFrame < track.spans[next].startFrame)) next = s;
                if (span.endFrame <= frame && (previous < 0 || span.endFrame > track.spans[previous].endFrame)) previous = s;
            }
            return next >= 0 ? next : previous;
        }

private void DrawCapsule(Capsule2D capsule, float z, Material material, float scale)
        {
            var a = new Vector3(capsule.a.x * scale, capsule.a.y * scale, z);
            var b = new Vector3(capsule.b.x * scale, capsule.b.y * scale, z);
            float radius = capsule.radius * scale;
            var ballScale = Vector3.one * radius;

            _utility.DrawMesh(_sphereMesh, Matrix4x4.TRS(a, Quaternion.identity, ballScale), material, 0);
            _utility.DrawMesh(_sphereMesh, Matrix4x4.TRS(b, Quaternion.identity, ballScale), material, 0);

            var axis = b - a;
            float length = axis.magnitude;
            if (length < 1e-4f) return;

            var rotation = Quaternion.FromToRotation(Vector3.up, axis / length);
            var meshScale = new Vector3(radius, length * 0.5f, radius);
            _utility.DrawMesh(_cylinderMesh, Matrix4x4.TRS((a + b) * 0.5f, rotation, meshScale), material, 0);
        }

        /// <summary>
        /// Материал из пула на каждый hitbox кадра: DrawMesh только ставит вызов в очередь,
        /// а рендер идёт позже, поэтому один общий материал с разными цветами не годится.
        /// </summary>
        private Material NextHitMaterial(Color color)
        {
            if (_hitMaterialsUsed == _hitMaterials.Count)
                _hitMaterials.Add(new Material(Shader.Find("Standard")) { hideFlags = HideFlags.HideAndDontSave });
            var material = _hitMaterials[_hitMaterialsUsed++];
            material.color = color;
            return material;
        }

        /// <summary>Подписи поверх картинки: кто где и какой кадр показан.</summary>
/// <summary>Подписи поверх картинки: кадр, позиции, зазор тел и контакт.</summary>
        private static void DrawOverlay(Rect rect, FrameDateMove move, int frame, FrameDateSimulation sim)
        {
            var label = new GUIStyle(EditorStyles.miniLabel)
            {
                normal = { textColor = new Color(0.85f, 0.85f, 0.88f) }
            };

            GUI.Label(new Rect(rect.x + 6f, rect.yMax - 18f, 120f, 16f), "Атакующий", label);

            var right = new GUIStyle(label) { alignment = TextAnchor.MiddleRight };
            GUI.Label(new Rect(rect.xMax - 126f, rect.yMax - 18f, 120f, 16f), "Цель", right);

            var frameStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                normal = { textColor = new Color(0.92f, 0.92f, 0.95f) }
            };
            GUI.Label(
                new Rect(rect.x + 6f, rect.y + 4f, 260f, 18f),
                $"кадр {frame}   {move.FrameToSeconds(frame):0.###} с   {move.frameRate} FPS",
                frameStyle);

            if (sim.frameCount == 0) return;
            int f = Mathf.Clamp(frame, 0, sim.frameCount - 1);

            float xa = sim.attackerX[f];
            float xt = sim.targetX[f];
            float gap = (xt - sim.HurtboxAt(move, Actor.Target, f).radius) - (xa + sim.HurtboxAt(move, Actor.Attacker, f).radius);
            string info = $"А x {xa:0.#}   Ц x {xt:0.#}   между центрами {xt - xa:0.#}   зазор тел {gap:0.#}   (Units)";
            if (sim.pushboxBlocked[f]) info += "   · пушбоксы упёрлись";
            if (sim.attackerAtLimit[f] || sim.targetAtLimit[f]) info += sim.cameraEngaged[f] ? "   · упор в камерную границу" : "   · упор в стену";
            else if (sim.cameraEngaged[f]) info += "   · камерные границы включены";
            if (sim.wallRecoil[f]) info += "   · отдача от стены";
            GUI.Label(new Rect(rect.x + 6f, rect.y + 22f, rect.width - 12f, 16f), info, label);

            string contactLine = null;
            for (int i = 0; i < sim.contacts.Count; i++)
            {
                var c = sim.contacts[i];
                if (c.frame != f) continue;
                var track = move.tracks[c.trackIndex];
                var span = track.spans[c.spanIndex];
                contactLine = $"КОНТАКТ · «{track.name}» [{span.startFrame},{span.endFrame}) · " +
                              (string.IsNullOrEmpty(c.reaction)
                                  ? $"отталкивание {c.pushDistance:0.#} Units за {c.pushEndFrame - c.frame} кадр."
                                  : $"реакция {c.reaction}");
                break;
            }

            FrameDateReactionCatalog.Reaction activeReaction;
            int reactionFrame;
            FrameDateSimulation.ReactionPlay activePlay;
            if (sim.ActiveReaction(move, f, out activeReaction, out reactionFrame, out activePlay))
            {
                var reactionStyle = new GUIStyle(EditorStyles.boldLabel) { normal = { textColor = new Color(0.55f, 0.80f, 1f) } };
                GUI.Label(new Rect(rect.x + 6f, rect.y + 56f, rect.width - 12f, 18f),
                    $"Цель: {FrameDateReactionCatalog.KindLabel(activeReaction)} · {activeReaction.name} · кадр {reactionFrame + 1} из {activeReaction.frameCount}", reactionStyle);
            }

            if (contactLine != null)
            {
                var hit = new GUIStyle(EditorStyles.boldLabel) { normal = { textColor = new Color(1f, 0.85f, 0.35f) } };
                GUI.Label(new Rect(rect.x + 6f, rect.y + 38f, rect.width - 12f, 18f), contactLine, hit);
            }
            else if (sim.contacts.Count > 0)
            {
                var frames = new System.Text.StringBuilder();
                for (int i = 0; i < sim.contacts.Count; i++)
                    frames.Append(i == 0 ? "" : ", ").Append(sim.contacts[i].frame);
                GUI.Label(new Rect(rect.x + 6f, rect.y + 38f, rect.width - 12f, 16f), "контакты на кадрах: " + frames, label);
            }
            else
            {
                GUI.Label(new Rect(rect.x + 6f, rect.y + 38f, rect.width - 12f, 16f), "контактов нет", label);
            }
        }

public void Dispose()
        {
            if (_utility != null)
            {
                _utility.Cleanup();
                _utility = null;
            }

            if (_attackerMaterial != null) UnityEngine.Object.DestroyImmediate(_attackerMaterial);
            if (_targetMaterial != null) UnityEngine.Object.DestroyImmediate(_targetMaterial);
            _attackerMaterial = null;
            _targetMaterial = null;

            foreach (var material in _hitMaterials)
                if (material != null) UnityEngine.Object.DestroyImmediate(material);
            _hitMaterials.Clear();
        }
    }
}
