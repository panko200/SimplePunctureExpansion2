using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Controls;
using YukkuriMovieMaker.Exo;
using YukkuriMovieMaker.Player.Video;
using YukkuriMovieMaker.Plugin.Effects;

namespace SimplePunctureExpansion2
{
    [VideoEffect("簡易パンク・膨張2", ["加工"], ["Pucker", "Bloat", "Vector"])]
    internal class SimplePunctureExpansion2Effect : VideoEffectBase
    {
        public override string Label => "簡易パンク・膨張2";

        [Display(GroupName = "効果", Name = "強度", Description = "+(プラス)側：膨張(Bloat)、 -(マイナス)側：パンク(Pucker)")]
        [AnimationSlider("F2", "", -1.0f, 1.0f)]
        public Animation Strength { get; } = new Animation(0.5f, -1.0f, 1.0f);

        // ★追加：カーブの太さ調整
        [Display(GroupName = "効果", Name = "ふっくら感", Description = "花びらやトゲの太さを調整します。33%が標準で、上げると太く丸くなり、下げると鋭く細くなります。")]
        [AnimationSlider("F0", "%", 0, 200)]
        public Animation CurveTension { get; } = new Animation(33, 0, 200);

        [Display(GroupName = "描画設定", Name = "単色塗りつぶし", Description = "ON:図形中央の色で塗る / OFF:元の画像(テクスチャ)で塗る")]
        [ToggleSlider]
        public bool UseSolidColor { get => _useSolidColor; set => Set(ref _useSolidColor, value); }
        private bool _useSolidColor = true;

        [Display(GroupName = "描画設定", Name = "テクスチャを歪ませる", Description = "単色OFF時、テクスチャ自体も枠の変形に合わせて引っ張るように歪ませます。")]
        [ToggleSlider]
        public bool DistortTexture { get => _distortTexture; set => Set(ref _distortTexture, value); }
        private bool _distortTexture = false;

        [Display(GroupName = "描画設定", Name = "テクスチャ歪み量", Description = "歪みの強さを調整します。100%で枠の変形と完全に一致します。")]
        [AnimationSlider("F0", "%", -200, 200)]
        public Animation TextureDistortion { get; } = new Animation(100, -200, 200);

        [Display(GroupName = "解析設定(頂点削減)", Name = "単純化の強さ", Description = "値を上げるほど、直線の途中にある余分な頂点が強力に削除されます。")]
        [AnimationSlider("F3", "", 0.0f, 0.2f)]
        public Animation Simplification { get; } = new Animation(0.05f, 0.0f, 0.5f);

        [Display(GroupName = "解析設定(頂点削減)", Name = "角の許容角度", Description = "この角度以下の緩やかな曲がりは直線とみなして頂点を削除します。")]
        [AnimationSlider("F0", "°", 0, 90)]
        public Animation CornerThreshold { get; } = new Animation(30, 0, 90);

        [Display(GroupName = "解析設定", Name = "不透明度閾値", Description = "図形とみなすアルファ値の境界(0-255)")]
        [AnimationSlider("F1", "", 1, 255)]
        public Animation AlphaThreshold { get; } = new Animation(10f, 1f, 255f);

        [Display(GroupName = "解析設定", Name = "変形基準点", Description = "ON:パス全体の中央から変形 / OFF:各パス(文字等)の中央から個別に変形")]
        [ToggleSlider]
        public bool UseGlobalCenter { get => _useGlobalCenter; set => Set(ref _useGlobalCenter, value); }
        private bool _useGlobalCenter = false;

        public override IEnumerable<string> CreateExoVideoFilters(int keyFrameIndex, ExoOutputDescription exoOutputDescription) => [];
        public override IVideoEffectProcessor CreateVideoEffect(IGraphicsDevicesAndContext devices) => new SimplePunctureExpansion2EffectProcessor(devices, this);
        // ★ CurveTension をアニメーション対象に追加
        protected override IEnumerable<IAnimatable> GetAnimatables() => [Strength, CurveTension, TextureDistortion, Simplification, CornerThreshold, AlphaThreshold];
    }
}