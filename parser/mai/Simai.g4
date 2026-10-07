/*
 * Simai的ANTLR4语法定义。
 * Simai官方文档：https://w.atwiki.jp/simai/pages/1002.html
 * 本语法同时实现了一些“常见非标准语法”以确保解析的鲁棒性。
 */
grammar Simai;

options { language=CSharp; }

// ---------------------------------------------------------------------------
// 词法
// ---------------------------------------------------------------------------

WS: [ \t\r\n]+ -> channel(HIDDEN);
COMMENT: '||' ~[\r\n]* -> channel(HIDDEN);

COMMA: ',';
// Numeric visual radius, separate from Hold/slide duration brackets.
TOUCH_RADIUS: '~[' [0-9eE+.\- \t]+ ']';
// A slash separates simultaneous notes in Alpha; subfolders use backslashes.
NOTE_SKIN: '~[' ~('[' | ']' | '/' | '\r' | '\n')+ ']'
    { if (AquaMai.Alpha053.Core.SlidePathParser.TryReadTrajectoryBorrow(Text, 0, out _, out _)) Type = BORROW_PATH; };
// Nested brackets belong to the complete borrowed slide. Skin and numeric
// radius tokens above retain precedence for a non-nested body.
BORROW_PATH: '~' BORROW_BRACKET;
fragment BORROW_BRACKET: '[' (BORROW_BRACKET | ~('[' | ']' | '/' | '\r' | '\n'))* ']';
noteSkin: NOTE_SKIN;
radiusOverride: TOUCH_RADIUS;

TAP_TO_STAR: '$$' | '$';
OVERLAY_STREAM: '@*' .*? '*@' | '@{' ~[\r\n]*; // Atomic independent stream; its commas never advance the main chart.
WAVE_TIME_SIG: '@' [0-9]+ '/' [0-9]+; // 波形拍号：@分子/分母，仅影响编辑器波形网格，对输出无影响
STAR_TO_TAP: '@';
NO_STAR: '?' | '!';

KEY: [1-8];
TOUCH_AREA: 'A' [1-8] | 'B' [1-8] | 'C' [1-2]? | 'D' [1-8] | 'E' [1-8];
NOISE_ZONE: 'X' ([AaBbDdEe] [1-8] | [Cc]);
// D 区位置（Majdata 新版 touch slide 语法）：7d = 与环键 7 同编号的 D 区点，对应 AquaMai code 的 'D'+数字。
// 必须放在 KEY 之后：ANTLR 最长匹配优先，'8d' 会整体匹配本规则而不是 KEY('8')+未定义'd'。
D_ZONE: [1-8] 'd';
// AquaMai/Majdata 自定义滑条 code（Majdata 的 SC shape）：位置开头、中间至少一个命令字符（大写 Q/P/K）、以数字位置结尾，
// 例如 3Q5K7、7Q1K3。标准 simai 中大写 Q/P/K 不作为记号出现（形状用的是小写 p/q），故不会误吞普通谱面。
SLIDE_CODE: [1-8A-E] [0-9A-EPQK<>^-]* 'K' [0-9]; // Legacy SC requires its terminal K; P/Q without K is a selectable orbit.

SLIDE_TYPE: '-' | 'v' | '<'+ | '>'+ | '^' | 'p' | 'q' | 'pp' | 'qq' | 's' | 'z' | 'w' | 'rp' | 'rq' | 'V' KEY 'd'? | [PQ] ([0-9] | TOUCH_AREA);  // P/Q 后一个选择圈位置，再接终点
slideType: SLIDE_TYPE;

INT: [0-9];

int: (KEY | INT)+;
number: int ('.' int?)? | '.' int; // Alpha also accepts .25 and 2. in duration values.

CHART_END: 'E';// 谱面结束那个E

// 时间轴命令：<SV*2> <SV*tap=2,hold=0.75> <HS*1.2> <BOUNCE*8:1> <SPAWN*1.225> 等。
// Majdata commands end at the first '>' on the same line. A '<' in
// a TEXT body is ordinary content, not a nested command or slide.
COMMAND: '<' [A-Za-z]+ '*' ~[>\r\n]* '>';

MODIFIER: [bmxfc]; // c=SV 豁免；m=地雷。具体行为由语义层处理。
modifiers: (MODIFIER | TAP_TO_STAR | STAR_TO_TAP | NO_STAR)*;

// ---------------------------------------------------------------------------
// 语法
// ---------------------------------------------------------------------------

chart: (notations COMMA)* notations CHART_END? EOF;

// 同一时刻的所有标记，包括note标记、bpm标记、时间轴命令等等
notations: (bpmTag | absulouteStepTag | metTag | commandTag | overlayStream | waveTimeSig)* noteGroup?;
overlayStream: OVERLAY_STREAM;
waveTimeSig: WAVE_TIME_SIG;

// Main scanner ignores leading backticks; independent streams retain their
// empty fake-each groups and shift the first note by one 128th per backtick.
noteGroup: FALSE_EACH? note eachNote*;
FALSE_EACH: '`'+;
eachNote: sep=('/' | FALSE_EACH) note?;

bpmTag: (lp+='(')+ number (rp+=')')+;
absulouteStepTag: (lp+='{')+ '#' number (rp+='}')+;
metTag: (lp+='{')+ int (rp+='}')+;
commandTag: COMMAND;

note: noiseZone | borrowedNote | slide (sharedHeadSlide)* | slideCodeNote | tap | tapHold | KEY+ | hold | touchStar | touch | touchHold;
// A noise region is a timeline event, with the same explicit duration forms as a slide.
noiseZone: NOISE_ZONE slideDuration?;
borrowedNote: (KEY | D_ZONE | TOUCH_AREA) borrowedCarrierPart* BORROW_PATH (borrowedCarrierPart | BORROW_PATH)*;
borrowedCarrierPart: noteSkin | radiusOverride | MODIFIER | TAP_TO_STAR | STAR_TO_TAP | NO_STAR | 'h' | slideDuration;

// 自定义滑条 code 直通（3Q5K7[8:2] / 7Q1K3b[8:2]）：code 原样交给生成器，修饰符与时长照常解析。
// 时长与修饰符顺序不固定：3Q5K7[8:2]、7Q1K3b[8:2]、7Q1K3[8:2]b 都要能解析。
slideCodeNote: SLIDE_CODE slideDuration? modifiers slideDuration?; // tap+是因为，simai允许123这种语法、和1/2/3是等价的，但仅限tap之间。

tap: (KEY | D_ZONE) noteSkin? modifiers;

// touchstar 简写（AquaMai mod 扩展语法）：B4$ = B4 区的 touchstar（只有星头没有 slide 轨迹），
// $ 后跟修饰符：B4$m = 地雷 touchstar（MNSTP）、B4$b = 绝赞 touchstar（BRSTP）。
// $ 是 TAP_TO_STAR 词法（'$$' | '$'）；注意 B1$>8 这类"touch 头 + $ + slide 轨迹"仍走 slide
// 备选（slide 在前且能匹配），不会被这里拦截。
touchStar: TOUCH_AREA (radiusOverride | noteSkin)? TAP_TO_STAR modifiers;

// 出于兼容性（以及simai本身设计的不合理？）考虑，会到处放置很多的modifiers以确保都能解析，解析的时候要把所有的modifiers取并集。
hold: (KEY | D_ZONE) noteSkin? modifiers 'h' modifiers (duration modifiers)?;

// 7[4:2] 省略 h 的 hold 简写（simai 常见非标准写法；官方用 7h[4:2]）
tapHold: (KEY | D_ZONE) modifiers duration modifiers;

touch: TOUCH_AREA (radiusOverride | noteSkin)? modifiers;

touchHold: TOUCH_AREA noteSkin? modifiers 'h' modifiers (duration modifiers)?;

duration: (lp+='[')+ (beats | '#' number) (rp+=']')+;
beats: int ':' int;
    
slideDuration: (lp+='[')+ (
        beats
        | '#' number
        | waitTime '##' asBpm '#' (beats | number)
        | waitTime '##' (beats | number)
        | asBpm '#' (beats | number)
    ) (rp+=']')+;
waitTime: number;
asBpm: number;

// slide的起点和每一段的终点都支持touch区（A1/B1/C/E2/D5等），用于AquaMai mod的NMSSS自定义slide。
slide: (tap | touchHead | holdSlideHead | touchHoldSlideHead) slideBody;
// touch 头 slide 的起点：touch 区（A1/B2/C/E5）或 D 区位置（7d，Majdata 新版语法）。
touchHead: TOUCH_AREA modifiers;
// * 后跟可选起点键：官方 simai 里 * 后直接跟形状=同头第二条路径（起点=星头键）；*5-3 带起点键=链段续写
// （起点=该键，合法谱面中=上一段终点）。没有 (KEY | TOUCH_AREA)? 时 *5-3 的 5 会被错误恢复吞掉。
sharedHeadSlide: '*' (KEY | TOUCH_AREA | D_ZONE)? slideBody;

slideBody // 根据Simai文档规定，分为两种情况。段间允许穿插modifiers（如?无头标记，常见写法如 2^4?rp2[...]）
    : slideType (KEY | TOUCH_AREA | D_ZONE) (modifiers slideType (KEY | TOUCH_AREA | D_ZONE))* modifiers slideDuration modifiers // 只有最后一段星星有时间指定
    | slideType (KEY | TOUCH_AREA | D_ZONE) (modifiers slideDuration slideType (KEY | TOUCH_AREA | D_ZONE))* modifiers slideDuration modifiers // 每一段星星都有独立的时间指定
    ;

// Hold-slide shorthand has no independent hold duration: its head lasts until
// the first slide starts. The bracket after the route belongs to the slide.
holdSlideHead: (KEY | D_ZONE) noteSkin? modifiers 'h' modifiers (duration modifiers)?;

// The head duration controls launch; an absent duration retains the slide's
// usual one-beat wait (or its authored explicit delay).
touchHoldSlideHead: TOUCH_AREA noteSkin? modifiers 'h' modifiers (duration modifiers)?;
