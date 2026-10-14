# MajdataAlpha 0.6.0 转谱兼容

基于 GitHub `539b89a1376e0881618dfc9972ec2e09a019949a`，保留本地尚未上传的 Alpha 模式标签写入、标签读取、对应测试及 SINMAI_ALPHA.md。

新增支持：

- `blockarea` / `noise` 的 SV、HS，以及 COLOR/SIZE/ALPHA 和 V 形式。全局 SV/HS 影响噪域，全局外观不影响噪域。
- ROTATE 最后可写布尔值决定 PV 是否随转，省略为 False。
- KALEIDOSCOPE 参数为角度，允许负数和 0°；0° 不等于关闭。
- JUDGELINEEXPAND 的 True/False 形式，保留旧二参数形式。
- SHOWJUDGETEXT 等显示命令的 Default 解析与恢复。原来忽略的 ShowJudgeInfo、ShowComboInfo、OuterBrightness 继续忽略。

新编码为 EF3、NZ2、JX2；游戏端需使用同批 Sinmai-Alpha 更新。旧编码继续可读取。另补齐了 MA2Parser 对 NZONE 的读取，使噪域可以再次导入和转换。

相邻键直线、Alpha 模式标签和预览旁车文件保留。本次不修改普通 MuConvert 仓库。

示例：

```text
(120){4}
<ROTATE*(True,90,1,False)>
<KALEIDOSCOPE*(True,-45,0)>
<JUDGELINEEXPAND*(True,1,CW)>
<COLOR*noise=#FF0000><HS*noise=2>XA1[4:1]/1,
<COLORV*noise=#00FF00>2,
<JUDGELINEEXPAND*(False)><KALEIDOSCOPE*(False)>
<SHOWJUDGETEXT*(Default,0.5)>1-2[4:1],E
```

新增 14 项回归测试通过。完整测试共 275 项：265 通过、10 失败；10 项失败在修改前基线中全部存在，没有新增失败。
