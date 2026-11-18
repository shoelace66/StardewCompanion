$ErrorActionPreference='Stop'
$root=Join-Path (Split-Path -Parent $PSScriptRoot) 'docs/DeepSeek逐工具实机验收-2026-11-10'
$manifest=[IO.File]::ReadAllText((Join-Path $root 'manifest.json'))|ConvertFrom-Json
$lines=[Collections.Generic.List[string]]::new()
$lines.Add('# DeepSeek 逐工具实机验收矩阵')
$lines.Add('')
$lines.Add('首轮实机验收进行中。所有游戏工具均由 DeepSeek 选择并调用；命令行仅用于环境准备、发送测试要求、暂停与核验。全部检查完成后统一修复，再复测改动方法及相关路径。')
$lines.Add('')
$lines.Add(('更新时间：'+[DateTimeOffset]::Now.ToString('o')))
$lines.Add(('总计 '+$manifest.totalTools+' 个工具身份、'+$manifest.totalCases+' 个用例；通过 '+@($manifest.cases|Where-Object status -eq 'passed').Count+'，失败 '+@($manifest.cases|Where-Object status -eq 'failed').Count+'，待完成 '+@($manifest.cases|Where-Object status -eq 'pending').Count+'。'))
$lines.Add('')
$lines.Add('| 编号 | 工具/子技能 | 正常用例数 | 失败或不可用用例数 | 通过 | 失败 | 待完成 |')
$lines.Add('| --- | --- | ---: | ---: | ---: | ---: | ---: |')
foreach($tool in $manifest.tools){
 $cases=@($manifest.cases|Where-Object tool -eq $tool.name)
 $lines.Add(('| {0} | `{1}` | {2} | {3} | {4} | {5} | {6} |' -f $tool.id,$tool.name,$tool.successCount,$tool.failureCount,@($cases|Where-Object status -eq 'passed').Count,@($cases|Where-Object status -eq 'failed').Count,@($cases|Where-Object status -eq 'pending').Count))
}
$lines.Add('')
$lines.Add('逐例结论见 manifest.json；人工语义核验记录见 reviews.jsonl；问题及修复复测列见 问题清单.md。evidence/round-1 保留原始模型请求、调用与回执；各用例前后观察和测试前置条件保存在对应隔离运行目录 acceptance/round-1 下。')
[IO.File]::WriteAllLines((Join-Path $root '验收矩阵.md'),$lines,[Text.UTF8Encoding]::new($false))
