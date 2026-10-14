# Sinmai-Alpha 转谱支持

支持当前 Alpha 扩展：TouchStar、Hold/TouchHold 滑条头、相邻键直线、SSS/D 区滑条、SlideCode、SV/HS、弹跳/出生/消失、独立流、假音符、噪域、字幕及屏幕特效。旧 SC 保留原行为。
仍可使用普通 MA2；扩展记录需安装 Sinmai-Alpha。

`ShowJudgeInfo`、`ShowComboInfo`、`OuterBrightness` 自动忽略，不改变音符或时间轴。
`ComboDisplay` 支持 None/Off、Combo、Score、Achievement/Acc、AccDown、DxAcc、DxAccDown、DxScore、CScore、CScoreDedxDown、Default。
`Default` 恢复游戏玩家设置；ComboDisplay 时间参数接受秒及分数拍，模式直接切换。

CLI 转换 maidata 时同时输出与 MA2 校验值绑定的 `.alpha-preview.json`，供 MCM-Alpha 预览。
`&first` 和可选前导小节同时作用于输出 MA2 和预览偏移。不含独立媒体工程时保留完整原始谱面预览数据。
谱面更新时应一起更新同名 MA2 和预览文件。音频/视频没有改变时无需重新转码。
