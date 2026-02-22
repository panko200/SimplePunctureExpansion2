using BitmapToVector;
using BitmapToVector.SkiaSharp;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Numerics;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.Direct2D1.Effects;
using Vortice.DXGI;
using Vortice.Mathematics;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Player.Video;

namespace SimplePunctureExpansion2
{
    internal class SimplePunctureExpansion2EffectProcessor : IVideoEffectProcessor
    {
        private readonly IGraphicsDevicesAndContext _devices;
        private readonly SimplePunctureExpansion2Effect _item;
        private ID2D1Image? _input;
        private ID2D1Bitmap1? _cpuReadBitmap;
        private ID2D1Bitmap? _outD2DBitmap;
        private AffineTransform2D? _mapTransformEffect;
        private ID2D1Image? _transformOutput;
        private bool _isEffectReady = false;

        private class SubPath
        {
            public List<SKPoint> Points = new List<SKPoint>();
            public bool Closed = false;
        }

        public SimplePunctureExpansion2EffectProcessor(IGraphicsDevicesAndContext devices, SimplePunctureExpansion2Effect item)
        {
            _devices = devices;
            _item = item;
            var d2dContext = _devices.DeviceContext;
            _mapTransformEffect = new AffineTransform2D(d2dContext);
            _transformOutput = _mapTransformEffect.Output;
        }

        public ID2D1Image Output => (_isEffectReady && _transformOutput != null) ? _transformOutput : (_input ?? throw new NullReferenceException("Input is null"));
        public void SetInput(ID2D1Image? input) { _input = input; }

        public DrawDescription Update(EffectDescription effectDescription)
        {
            _isEffectReady = false;
            if (_input == null) return effectDescription.DrawDescription;

            var d2dContext = _devices.DeviceContext;
            var frame = effectDescription.ItemPosition.Frame;
            var length = effectDescription.ItemDuration.Frame;
            var fps = effectDescription.FPS;

            float strength = (float)_item.Strength.GetValue(frame, length, fps);
            float tension = (float)_item.CurveTension.GetValue(frame, length, fps) / 100f;
            int threshold = (int)_item.AlphaThreshold.GetValue(frame, length, fps);
            float simpEpsilon = (float)_item.Simplification.GetValue(frame, length, fps);
            float cornerDeg = (float)_item.CornerThreshold.GetValue(frame, length, fps);

            bool useGlobalCenter = _item.UseGlobalCenter;
            bool useSolidColor = _item.UseSolidColor;
            bool distortTexture = _item.DistortTexture;
            float texDistAmount = (float)_item.TextureDistortion.GetValue(frame, length, fps) / 100f;

            Vortice.RawRectF rawBounds;
            try { rawBounds = d2dContext.GetImageLocalBounds(_input); } catch { return effectDescription.DrawDescription; }

            int left = (int)Math.Floor(rawBounds.Left);
            int top = (int)Math.Floor(rawBounds.Top);
            int right = (int)Math.Ceiling(rawBounds.Right);
            int bottom = (int)Math.Ceiling(rawBounds.Bottom);
            int width = right - left;
            int height = bottom - top;

            if (width <= 0 || height <= 0) return effectDescription.DrawDescription;

            PrepareCpuReadBitmap(d2dContext, width, height, rawBounds);

            var map = _cpuReadBitmap!.Map(MapOptions.Read);
            try
            {
                ProcessVectorPuckerBloat(d2dContext, map, width, height, strength, tension, threshold, simpEpsilon, cornerDeg, useGlobalCenter, useSolidColor, distortTexture, texDistAmount);

                if (_mapTransformEffect != null && _outD2DBitmap != null)
                {
                    int padX = (int)(width * Math.Abs(strength));
                    int padY = (int)(height * Math.Abs(strength));
                    _mapTransformEffect.SetInput(0, _outD2DBitmap, true);
                    _mapTransformEffect.TransformMatrix = Matrix3x2.CreateTranslation(left - padX, top - padY);
                    _isEffectReady = true;
                }
            }
            finally { _cpuReadBitmap.Unmap(); }

            return effectDescription.DrawDescription;
        }

        private void PrepareCpuReadBitmap(ID2D1DeviceContext mainContext, int width, int height, Vortice.RawRectF bounds)
        {
            if (_cpuReadBitmap == null || _cpuReadBitmap.PixelSize.Width != width || _cpuReadBitmap.PixelSize.Height != height)
            {
                _cpuReadBitmap?.Dispose();
                var props = new BitmapProperties1(new PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied), 96, 96, BitmapOptions.CpuRead | BitmapOptions.CannotDraw);
                _cpuReadBitmap = mainContext.CreateBitmap(new SizeI(width, height), IntPtr.Zero, 0, props);
            }

            using (var localContext = _devices.DeviceContext.Device.CreateDeviceContext(DeviceContextOptions.None))
            using (var gpuBitmap = localContext.CreateBitmap(new SizeI(width, height), new BitmapProperties1(new PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied), 96, 96, BitmapOptions.Target)))
            {
                localContext.Target = gpuBitmap;
                localContext.BeginDraw();
                localContext.Clear(new Color4(0, 0, 0, 0));
                localContext.DrawImage(_input, new Vector2(-bounds.Left, -bounds.Top));
                localContext.EndDraw();
                _cpuReadBitmap.CopyFromBitmap(gpuBitmap);
            }
        }

        private unsafe void ProcessVectorPuckerBloat(ID2D1DeviceContext context, Vortice.Direct2D1.MappedRectangle map, int width, int height, float strength, float tension, int alphaThreshold, float simpEpsilon, float cornerDeg, bool useGlobalCenter, bool useSolidColor, bool distortTexture, float texDistAmount)
        {
            var srcInfo = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var srcSkBitmap = new SKBitmap();
            srcSkBitmap.InstallPixels(srcInfo, map.Bits, map.Pitch);

            using var bwBitmap = new SKBitmap(width, height, SKColorType.Gray8, SKAlphaType.Opaque);
            byte* srcPtr = (byte*)map.Bits;
            byte* dstPtr = (byte*)bwBitmap.GetPixels();
            for (int y = 0; y < height; y++)
            {
                byte* srcRow = srcPtr + y * map.Pitch;
                byte* dstRow = dstPtr + y * bwBitmap.RowBytes;
                for (int x = 0; x < width; x++) dstRow[x] = srcRow[x * 4 + 3] > alphaThreshold ? (byte)0 : (byte)255;
            }

            var param = new PotraceParam { AlphaMax = 0.0, OptiCurve = false, OptTolerance = 0.0 };
            IEnumerable<SKPath> paths = PotraceSkiaSharp.Trace(param, bwBitmap);

            int padX = (int)(width * Math.Abs(strength));
            int padY = (int)(height * Math.Abs(strength));
            int outW = width + padX * 2;
            int outH = height + padY * 2;

            using var outBitmap = new SKBitmap(outW, outH, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var canvas = new SKCanvas(outBitmap);
            canvas.Clear(SKColors.Transparent);

            float anchorStrength = -strength;
            float controlStrength = strength;
            SKPoint globalCenter = new SKPoint(width / 2f, height / 2f);

            var localMatrix = SKMatrix.CreateTranslation(padX, padY);

            // 単色塗りつぶし用・または歪ませない時のシェーダー
            using var textureShader = SKShader.CreateBitmap(srcSkBitmap, SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, localMatrix);
            // メッシュワープ描画用のシェーダー (元のUV座標にマッピングするため平行移動なし)
            using var meshTextureShader = SKShader.CreateBitmap(srcSkBitmap, SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, SKMatrix.Identity);

            foreach (var path in paths)
            {
                SKPoint center = useGlobalCenter ? globalCenter : new SKPoint(path.Bounds.MidX, path.Bounds.MidY);
                SKPoint drawCenter = new SKPoint(center.X + padX, center.Y + padY);

                var subPaths = ExtractSubPaths(path);
                float radius = Math.Max(path.Bounds.Width, path.Bounds.Height) / 2f;
                float epsilon = Math.Max(0.1f, simpEpsilon);

                if (!useSolidColor && distortTexture)
                {
                    // ========================================================
                    // ★ 大成功したメッシュワープ描画
                    // ========================================================
                    float texDistAnchor = anchorStrength * texDistAmount;
                    float texDistControl = controlStrength * texDistAmount;
                    using var paint = new SKPaint { IsAntialias = true, Shader = meshTextureShader };

                    List<SKPoint> vertices = new();
                    List<SKPoint> texs = new();

                    foreach (var sub in subPaths)
                    {
                        if (sub.Points.Count < 3) continue;
                        if (sub.Points[0] == sub.Points[^1]) sub.Points.RemoveAt(sub.Points.Count - 1);

                        var shifted = ShiftPoints(sub.Points, center);
                        var simplified = RamerDouglasPeucker(shifted, epsilon);
                        simplified = RemoveStraightLineVertices(simplified, radius, cornerDeg);
                        if (simplified.Count > 1 && simplified[0] == simplified[^1]) simplified.RemoveAt(simplified.Count - 1);
                        if (simplified.Count < 3) continue;

                        int M = 10; // 曲線の分割数
                        for (int i = 0; i < simplified.Count; i++)
                        {
                            var p1 = simplified[i];
                            var p2 = simplified[(i + 1) % simplified.Count];

                            // ふっくら感(tension)を適用した制御点
                            var cp1 = new SKPoint(p1.X + (p2.X - p1.X) * tension, p1.Y + (p2.Y - p1.Y) * tension);
                            var cp2 = new SKPoint(p2.X - (p2.X - p1.X) * tension, p2.Y - (p2.Y - p1.Y) * tension);

                            var P1_prime = Morph(p1, center, texDistAnchor, padX, padY);
                            var CP1_prime = Morph(cp1, center, texDistControl, padX, padY);
                            var CP2_prime = Morph(cp2, center, texDistControl, padX, padY);
                            var P2_prime = Morph(p2, center, texDistAnchor, padX, padY);

                            for (int j = 0; j < M; j++)
                            {
                                float t1 = (float)j / M;
                                float t2 = (float)(j + 1) / M;

                                SKPoint uv1 = new SKPoint(p1.X + (p2.X - p1.X) * t1, p1.Y + (p2.Y - p1.Y) * t1);
                                SKPoint uv2 = new SKPoint(p1.X + (p2.X - p1.X) * t2, p1.Y + (p2.Y - p1.Y) * t2);

                                SKPoint v1 = EvalBezier(P1_prime, CP1_prime, CP2_prime, P2_prime, t1);
                                SKPoint v2 = EvalBezier(P1_prime, CP1_prime, CP2_prime, P2_prime, t2);

                                vertices.Add(drawCenter); vertices.Add(v1); vertices.Add(v2);
                                texs.Add(center); texs.Add(uv1); texs.Add(uv2);
                            }
                        }
                    }
                    if (vertices.Count > 0)
                    {
                        canvas.DrawVertices(SKVertexMode.Triangles, vertices.ToArray(), texs.ToArray(), null, paint);
                    }
                }
                else
                {
                    // ========================================================
                    // 単色塗りつぶし・テクスチャ歪みOFF時の処理 (EvenOdd)
                    // ========================================================
                    using var resultPath = new SKPath();
                    resultPath.FillType = SKPathFillType.EvenOdd;

                    foreach (var sub in subPaths)
                    {
                        if (sub.Points.Count < 3) continue;
                        if (sub.Points[0] == sub.Points[^1]) sub.Points.RemoveAt(sub.Points.Count - 1);

                        var shifted = ShiftPoints(sub.Points, center);
                        var simplified = RamerDouglasPeucker(shifted, epsilon);
                        simplified = RemoveStraightLineVertices(simplified, radius, cornerDeg);
                        if (simplified.Count > 1 && simplified[0] == simplified[^1]) simplified.RemoveAt(simplified.Count - 1);
                        if (simplified.Count < 3) continue;

                        resultPath.MoveTo(Morph(simplified[0], center, anchorStrength, padX, padY));
                        for (int i = 0; i < simplified.Count; i++)
                        {
                            var p1 = simplified[i];
                            var p2 = simplified[(i + 1) % simplified.Count];

                            // ふっくら感(tension)を適用した制御点
                            var cp1 = new SKPoint(p1.X + (p2.X - p1.X) * tension, p1.Y + (p2.Y - p1.Y) * tension);
                            var cp2 = new SKPoint(p2.X - (p2.X - p1.X) * tension, p2.Y - (p2.Y - p1.Y) * tension);

                            resultPath.CubicTo(
                                Morph(cp1, center, controlStrength, padX, padY),
                                Morph(cp2, center, controlStrength, padX, padY),
                                Morph(p2, center, anchorStrength, padX, padY)
                            );
                        }
                        if (sub.Closed) resultPath.Close();
                    }

                    using var paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
                    if (useSolidColor)
                    {
                        int cx = (int)Math.Clamp(center.X, 0, width - 1);
                        int cy = (int)Math.Clamp(center.Y, 0, height - 1);
                        SKColor pathColor = srcSkBitmap.GetPixel(cx, cy);
                        if (pathColor.Alpha == 0) pathColor = SKColors.White;
                        paint.Color = pathColor;
                    }
                    else paint.Shader = textureShader;

                    canvas.DrawPath(resultPath, paint);
                }
            }

            _outD2DBitmap?.Dispose();
            _outD2DBitmap = context.CreateBitmap(
                new SizeI(outW, outH), outBitmap.GetPixels(), outBitmap.RowBytes,
                new BitmapProperties(new PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied))
            );
        }

        private SKPoint EvalBezier(SKPoint p0, SKPoint p1, SKPoint p2, SKPoint p3, float t)
        {
            float u = 1f - t;
            float tt = t * t;
            float uu = u * u;
            float uuu = uu * u;
            float ttt = tt * t;
            float x = uuu * p0.X + 3 * uu * t * p1.X + 3 * u * tt * p2.X + ttt * p3.X;
            float y = uuu * p0.Y + 3 * uu * t * p1.Y + 3 * u * tt * p2.Y + ttt * p3.Y;
            return new SKPoint(x, y);
        }

        private List<SKPoint> ShiftPoints(List<SKPoint> points, SKPoint center)
        {
            float maxD = -1; int maxIdx = 0;
            for (int i = 0; i < points.Count; i++)
            {
                float dx = points[i].X - center.X; float dy = points[i].Y - center.Y;
                float d = dx * dx + dy * dy;
                if (d > maxD) { maxD = d; maxIdx = i; }
            }
            var shifted = new List<SKPoint>(points.Count);
            for (int i = 0; i < points.Count; i++) shifted.Add(points[(maxIdx + i) % points.Count]);
            shifted.Add(shifted[0]);
            return shifted;
        }

        private List<SubPath> ExtractSubPaths(SKPath path)
        {
            var list = new List<SubPath>();
            SubPath current = null;
            using var iterator = path.CreateIterator(false);
            SKPathVerb verb; SKPoint[] pts = new SKPoint[4];
            while ((verb = iterator.Next(pts)) != SKPathVerb.Done)
            {
                switch (verb)
                {
                    case SKPathVerb.Move:
                        if (current != null && current.Points.Count > 0) list.Add(current);
                        current = new SubPath(); current.Points.Add(pts[0]);
                        break;
                    case SKPathVerb.Line:
                        if (current != null) current.Points.Add(pts[1]);
                        break;
                    case SKPathVerb.Close:
                        if (current != null) { current.Closed = true; list.Add(current); current = null; }
                        break;
                }
            }
            if (current != null && current.Points.Count > 0) list.Add(current);
            return list;
        }

        private SKPoint Morph(SKPoint pt, SKPoint center, float s, float padX, float padY)
        {
            float dx = pt.X - center.X; float dy = pt.Y - center.Y;
            return new SKPoint(center.X + dx * (1f + s) + padX, center.Y + dy * (1f + s) + padY);
        }

        private List<SKPoint> RemoveStraightLineVertices(List<SKPoint> vertices, float radius, float thresholdDeg)
        {
            if (vertices.Count <= 3) return vertices;
            var result = new List<SKPoint>(vertices);
            bool removedAny = true;
            float minDistance = Math.Max(3.0f, radius * 0.05f);
            float dotThreshold = (float)Math.Cos(thresholdDeg * Math.PI / 180.0);

            while (removedAny && result.Count > 3)
            {
                removedAny = false;
                for (int i = 0; i < result.Count - 1; i++)
                {
                    if (result.Count <= 3) break;
                    int prevIdx = (i - 1 + result.Count) % result.Count;
                    if (prevIdx == result.Count - 1 && result[prevIdx] == result[0]) prevIdx--;
                    int nextIdx = (i + 1) % result.Count;
                    if (nextIdx == 0 && result[nextIdx] == result[^1]) nextIdx = 1;

                    Vector2 v1 = Vector2.Normalize(new Vector2(result[i].X - result[prevIdx].X, result[i].Y - result[prevIdx].Y));
                    Vector2 v2 = Vector2.Normalize(new Vector2(result[nextIdx].X - result[i].X, result[nextIdx].Y - result[i].Y));
                    if (Vector2.Dot(v1, v2) > dotThreshold) { result.RemoveAt(i); removedAny = true; i--; continue; }

                    float distPrev = SKPoint.Distance(result[prevIdx], result[i]);
                    float distNext = SKPoint.Distance(result[i], result[nextIdx]);
                    if (distPrev < minDistance || distNext < minDistance) { result.RemoveAt(i); removedAny = true; i--; }
                }
            }
            return result;
        }

        private List<SKPoint> RamerDouglasPeucker(List<SKPoint> pointList, float epsilon)
        {
            if (pointList.Count < 2) return pointList;
            float dmax = 0; int index = 0, end = pointList.Count - 1;
            for (int i = 1; i < end; i++)
            {
                float area = Math.Abs(0.5f * (pointList[0].X * pointList[end].Y + pointList[end].X * pointList[i].Y + pointList[i].X * pointList[0].Y - pointList[end].X * pointList[0].Y - pointList[i].X * pointList[end].Y - pointList[0].X * pointList[i].Y));
                float bottom = SKPoint.Distance(pointList[0], pointList[end]);
                float d = bottom == 0 ? SKPoint.Distance(pointList[i], pointList[0]) : (area * 2.0f) / bottom;
                if (d > dmax) { index = i; dmax = d; }
            }
            List<SKPoint> resultList;
            if (dmax > epsilon)
            {
                var recResults1 = RamerDouglasPeucker(pointList.GetRange(0, index + 1), epsilon);
                var recResults2 = RamerDouglasPeucker(pointList.GetRange(index, end - index + 1), epsilon);
                recResults1.RemoveAt(recResults1.Count - 1);
                resultList = new List<SKPoint>(recResults1);
                resultList.AddRange(recResults2);
            }
            else resultList = new List<SKPoint> { pointList[0], pointList[end] };
            return resultList;
        }

        public void ClearInput() { _input = null; _isEffectReady = false; _mapTransformEffect?.SetInput(0, null, true); }
        public void Dispose()
        {
            _transformOutput?.Dispose(); _transformOutput = null;
            _mapTransformEffect?.Dispose(); _mapTransformEffect = null;
            _cpuReadBitmap?.Dispose(); _cpuReadBitmap = null;
            _outD2DBitmap?.Dispose(); _outD2DBitmap = null;
        }
    }
}