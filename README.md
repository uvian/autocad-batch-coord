# BatchCoord — AutoCAD 批量坐标标注插件

替代湘源控规批量坐标功能，自动避让不重叠。

## 功能

输 `BATCHCOORD` 或 `BZ` 回车：

1. 框选多个地块（闭合多段线）
2. 输入小数位数（默认 3）
3. 输入字高（回车自动按图纸比例计算，可手动覆盖）

自动为每个角点生成带引线的 `X=xxx.xxx` / `Y=xxx.xxx` 坐标标注，智能避让不重叠。

## 安装

### Bundle 插件包（推荐）

1. 下载 `BatchCoord.bundle.zip`
2. 解压得到 `BatchCoord.bundle` 文件夹
3. 复制到 `C:\ProgramData\Autodesk\ApplicationPlugins\`
4. 重启 AutoCAD
5. 输 `BATCHCOORD` 或 `BZ` 运行

### 手动 NETLOAD

1. 下载 `BatchCoord.dll`
2. AutoCAD 输 `NETLOAD` 选中 `.dll`
3. 输 `BZ` 运行

## 图层结构

- 坐标文字：黄色，`MText`（两行：`X=...` / `Y=...`）
- 角点标记：黄色小圆点
- 引线：黄色折线

所有元素颜色随层，在图层管理器修改颜色即可统一切换。

## 兼容性

| 平台 | 兼容 |
|------|------|
| AutoCAD 2019~2025 | ✅ 已测试 2020 |
| 中望CAD 2020 | ✅ AutoCAD 兼容模式 |
