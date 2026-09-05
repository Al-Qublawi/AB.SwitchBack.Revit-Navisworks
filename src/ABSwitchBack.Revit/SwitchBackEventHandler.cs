using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ABSwitchBack.Core;
using ABSwitchBack.Core.Interop;

namespace ABSwitchBack.Revit
{
    /// <summary>
    /// Runs on the Revit UI thread via ExternalEvent. The pipe listener only ever
    /// enqueues an id and raises the event - it never touches the Revit API itself.
    /// </summary>
    public sealed class SwitchBackEventHandler : IExternalEventHandler
    {
        private const double MmPerFoot = 304.8;

        private readonly ConcurrentQueue<long> _pending = new ConcurrentQueue<long>();

        /// <summary>Thread-safe. Called from the named pipe background thread.</summary>
        public void Enqueue(long elementId)
        {
            _pending.Enqueue(elementId);
        }

        public string GetName()
        {
            return "AB SwitchBack - focus element";
        }

        public void Execute(UIApplication app)
        {
            // Coalesce: if several clicks arrived while Revit was busy, honour only the
            // newest one. Showing four dialogs in a row would be hostile.
            long elementId = -1;
            long next;
            while (_pending.TryDequeue(out next)) elementId = next;
            if (elementId < 0) return;

            try
            {
                Focus(app, elementId);
            }
            catch (Exception ex)
            {
                Log.Error("SwitchBack failed while focusing element " + elementId, ex);
                ShowDialog("SwitchBack could not focus that element.",
                           ex.Message + Environment.NewLine + Environment.NewLine +
                           "See the log at " + Paths.LogsDir);
            }
        }

        private void Focus(UIApplication app, long rawId)
        {
            SwitchBackConfig cfg = SwitchBackConfig.Load();

            UIDocument uidoc = app.ActiveUIDocument;
            if (uidoc == null || uidoc.Document == null)
            {
                ShowDialog("No active Revit project.",
                           "SwitchBack received element " + rawId +
                           " but no document is open in this Revit instance.");
                return;
            }

            Document doc = uidoc.Document;

            // Deliberately the ACTIVE document only. Linked models are ignored by design:
            // an id taken from a link would resolve to a different, wrong element here.
            ElementId eid = RevitCompat.ToElementId(rawId);
            if (eid == ElementId.InvalidElementId)
            {
                ShowDialog("Invalid element id.",
                           rawId + " is not a valid Revit element id for this version.");
                return;
            }

            Element element = doc.GetElement(eid);
            if (element == null)
            {
                ShowDialog("Element not found.",
                           "Element id " + rawId + " does not exist in the active project:" + Environment.NewLine +
                           doc.Title + Environment.NewLine + Environment.NewLine +
                           "Check that the Navisworks model came from this same Revit project. " +
                           "Linked models are not searched.");
                return;
            }

            Log.Info("Focusing element " + rawId + " (" + SafeName(element) + ") in " + doc.Title);

            View3D view = GetOrCreate3DView(doc, uidoc, cfg.CreateViewIfMissing);
            if (view != null && (uidoc.ActiveView == null || uidoc.ActiveView.Id != view.Id))
            {
                // Must happen outside any transaction.
                try { uidoc.ActiveView = view; }
                catch (Exception ex) { Log.Warn("Could not activate 3D view: " + ex.Message); }
            }

            double marginFt = Math.Max(0.0, cfg.SectionBoxMarginMm) / MmPerFoot;
            BoundingBoxXYZ modelBox = GetModelAlignedBox(element, view, marginFt);

            if (modelBox != null && cfg.CreateSectionBox && view != null && !doc.IsFamilyDocument)
                ApplySectionBox(doc, view, modelBox);

            try { uidoc.Selection.SetElementIds(new List<ElementId> { eid }); }
            catch (Exception ex) { Log.Warn("Could not set selection: " + ex.Message); }

            try { uidoc.RefreshActiveView(); }
            catch { }

            if (modelBox != null)
            {
                ZoomTo(uidoc, view, modelBox);
            }
            else
            {
                Log.Warn("Element " + rawId + " has no bounding box; selected without zoom or section box.");
                ShowDialog("Element selected.",
                           "Element id " + rawId + " was found and selected, but it has no geometry to zoom to. " +
                           "It may be a non-graphical element such as a type or a schedule.");
            }

            // The sender granted us the foreground right just before sending, so this works.
            WindowFocus.BringToFront(app.MainWindowHandle);
        }

        private static void ApplySectionBox(Document doc, View3D view, BoundingBoxXYZ box)
        {
            try
            {
                if (view.IsTemplate) return;

                using (var t = new Transaction(doc, "SwitchBack: section box"))
                {
                    t.Start();
                    view.IsSectionBoxActive = true;
                    view.SetSectionBox(box);
                    t.Commit();
                }
            }
            catch (Exception ex)
            {
                // A locked or otherwise unsuitable view must not abort the whole switch-back.
                Log.Warn("Could not apply section box: " + ex.Message);
            }
        }

        private static void ZoomTo(UIDocument uidoc, View3D view, BoundingBoxXYZ box)
        {
            try
            {
                ElementId targetView = view != null ? view.Id : uidoc.ActiveView.Id;
                foreach (UIView uv in uidoc.GetOpenUIViews())
                {
                    if (uv.ViewId != targetView) continue;
                    uv.ZoomAndCenterRectangle(box.Min, box.Max);
                    return;
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Could not zoom: " + ex.Message);
            }
        }

        /// <summary>
        /// Returns an axis-aligned, model-space box padded by marginFt, or null when the
        /// element has no geometry. Handles boxes whose Transform is not the identity
        /// (rotated families) by enveloping the eight transformed corners.
        /// </summary>
        private static BoundingBoxXYZ GetModelAlignedBox(Element element, View3D view, double marginFt)
        {
            BoundingBoxXYZ raw = null;
            try { raw = element.get_BoundingBox(null); }
            catch { }

            if (raw == null && view != null)
            {
                try { raw = element.get_BoundingBox(view); }
                catch { }
            }
            if (raw == null) return null;

            Transform t = raw.Transform ?? Transform.Identity;
            XYZ mn = raw.Min, mx = raw.Max;
            if (mn == null || mx == null) return null;

            double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;

            for (int i = 0; i < 8; i++)
            {
                var corner = new XYZ((i & 1) == 0 ? mn.X : mx.X,
                                     (i & 2) == 0 ? mn.Y : mx.Y,
                                     (i & 4) == 0 ? mn.Z : mx.Z);
                XYZ p = t.OfPoint(corner);
                if (p.X < minX) minX = p.X;
                if (p.Y < minY) minY = p.Y;
                if (p.Z < minZ) minZ = p.Z;
                if (p.X > maxX) maxX = p.X;
                if (p.Y > maxY) maxY = p.Y;
                if (p.Z > maxZ) maxZ = p.Z;
            }

            // Guarantee a non-degenerate box even for a point-like element.
            double pad = Math.Max(marginFt, 0.01);

            var result = new BoundingBoxXYZ();
            result.Transform = Transform.Identity;
            result.Min = new XYZ(minX - pad, minY - pad, minZ - pad);
            result.Max = new XYZ(maxX + pad, maxY + pad, maxZ + pad);
            return result;
        }

        private static View3D GetOrCreate3DView(Document doc, UIDocument uidoc, bool mayCreate)
        {
            try
            {
                // 1. The view the user is already in, when it is a usable 3D view.
                var active = uidoc.ActiveView as View3D;
                if (active != null && !active.IsTemplate) return active;

                // 2. An existing 3D view, preferring the default orthographic one.
                List<View3D> views = new FilteredElementCollector(doc)
                    .OfClass(typeof(View3D))
                    .Cast<View3D>()
                    .Where(v => v != null && !v.IsTemplate)
                    .ToList();

                View3D pick = views.FirstOrDefault(v => !v.IsPerspective && v.Name != null &&
                                                        v.Name.StartsWith("{3D", StringComparison.OrdinalIgnoreCase))
                           ?? views.FirstOrDefault(v => !v.IsPerspective)
                           ?? views.FirstOrDefault();
                if (pick != null) return pick;

                // 3. Nothing suitable exists. Creating a view is the only model write
                //    besides the section box, so it is opt-out.
                if (!mayCreate)
                {
                    Log.Warn("No usable 3D view and CreateViewIfMissing is false; not creating one.");
                    return null;
                }

                ViewFamilyType vft = new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewFamilyType))
                    .Cast<ViewFamilyType>()
                    .FirstOrDefault(x => x.ViewFamily == ViewFamily.ThreeDimensional);
                if (vft == null) return null;

                using (var t = new Transaction(doc, "SwitchBack: create 3D view"))
                {
                    t.Start();
                    View3D created = View3D.CreateIsometric(doc, vft.Id);
                    try
                    {
                        created.Name = "SwitchBack 3D " +
                            DateTime.Now.ToString("HHmmss", CultureInfo.InvariantCulture);
                    }
                    catch
                    {
                        // Duplicate name - keep whatever Revit assigned.
                    }
                    t.Commit();
                    return created;
                }
            }
            catch (Exception ex)
            {
                Log.Error("Could not obtain a 3D view.", ex);
                return null;
            }
        }

        private static string SafeName(Element e)
        {
            try { return e.Name ?? "unnamed"; }
            catch { return "unnamed"; }
        }

        private static void ShowDialog(string title, string body)
        {
            try
            {
                var d = new TaskDialog("AB SwitchBack");
                d.MainInstruction = title;
                d.MainContent = body;
                d.Show();
            }
            catch { }
        }
    }
}
