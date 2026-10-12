# test-instrument-probe

`src\InstrumentSpaceProbe.cs` 的离线判据测试。它**直接编译那一份源文件**
（见 probe-test.csproj 里的 `<Compile Include="..\..\src\InstrumentSpaceProbe.cs" />`），
所以测的就是将要发出去的那份代码，不是复制粘贴的副本。

## 跑法

    dotnet run -c Release --project tools\test-instrument-probe

期望输出 `PASS=10  FAIL=0`，退出码 0。

## 为什么需要它

这个探针要回答的是「VFE Empire 那句会崩的判断，作者修好了没有」——
答案决定本补丁是继续接管还是自动让路。它**判错的唯一坏方向**是
「明明还没修却说已经安全」，那会让保护被撤掉、游戏重新崩。

所以这里有 10 个用例，其中：

- **6 个字段形态**（真实情况：`placeWorkers` 是 `Verse.BuildableDef` 的公共字段，
  IL 里是 `ldfld`）：未修 / `!= null &&` / `?.` / 提前 return / 假修复 /
  静态扩展方法；
- **2 个属性形态**（防御：万一将来那个成员改成属性）；
- **1 个整体重写**（不再读 placeWorkers）→ 应当抛异常，由调用方走保守路径；
- **1 个用 `Reflection.Emit` 逐条复刻真实 VFEE lambda** 的方法 ——
  本机 VFE Empire 1.6 的真实 IL 是 22 字节 6 条指令
  （MVID `7b87b4b5f2e8419a9b66f287653fbf84` / 方法 `0x06000744`），
  这一条用来确认「真实形态」下判据给的是 `false(unsafe)`。

「假修复」那条尤其重要：`placeWorkers.Count > 0 && placeWorkers.Contains(...)`
看着像加了保护，其实对 `null` 照样崩 —— 判据不能被骗过去。
