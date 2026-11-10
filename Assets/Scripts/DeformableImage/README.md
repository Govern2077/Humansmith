# 双面可形变图片：第一阶段

这一阶段包含：

- 正反面两张 Sprite。
- 自动生成规则细分网格。
- 正反面使用同一套逻辑顶点，保证形变始终一致。
- 鼠标拖拽、膨胀、捏合与复原。
- 为后续闭合切割和布料模拟保留逻辑顶点、三角形与事件接口。

## 创建对象

1. 在 Project 窗口同时选中正面和反面两张 Sprite。选中顺序不可靠，所以创建后仍需在 Inspector 中确认 Front Sprite 和 Back Sprite。
2. 执行 `GameObject > 2D Object > Deformable Double-Sided Image`。
3. 在 `Deformable Image Mesh` 组件中设置：
   - Front Sprite：正面。
   - Back Sprite：反面。
   - Subdivisions X/Y：建议先用 32。
   - Height：图片在世界空间中的高度，建议 5。
   - Flip Back Horizontally：背面文字镜像时切换此项。
4. 点击 `Rebuild Mesh`。

## 摄像机

推荐把 Main Camera 设置为：

- Projection：Orthographic。
- Position：例如 `(0, 0, -10)`。
- 图片对象 Position：例如 `(0, 0, 0)`。

## 运行时操作

- `1`：拖拽。
- `2`：膨胀。按住左键并向上/下拖动。
- `3`：捏合。按住左键并向上/下拖动。
- `4`：切割。按住左键绘制轨迹。
- 鼠标滚轮：调整画笔半径。
- `R`：恢复初始网格。
- `Esc`：取消当前尚未松开的操作。

### 拖拽固定与回弹

- 使用 `1` 拖拽时，直接松开左键会以弹性动画回到本次拖拽开始前的位置。
- 拖拽过程中按一下鼠标中键，再松开左键，当前形状会固定。
- 回弹过程中按鼠标中键，会把回弹当时的形状冻结。
- `Rebound Duration / Damping / Oscillations` 分别控制回弹时间、阻尼与振动次数。

### 切割

- 按 `4` 切换到切割工具，按住左键绘制青色轨迹。
- 轨迹终点回到起点附近（默认 `Closed Loop Distance = 0.18`）时视为闭合切割。
- 闭合区域会从原图移除，并生成一个带真实三角网格的独立 GameObject；直接按住鼠标左键即可整体移动，切块会独占本次鼠标操作并自动显示到其他切块上方。
- 没有闭合的轨迹会生成一条圆角狭缝；狭缝两侧会被 Alpha 命中系统视为新的可翻折边缘。
- 开放和闭合切割都会对刀线经过的原有三角形执行 Difference，在切口边界插入新顶点并重建正反面 Mesh；刀口两侧不再共享跨缝三角形或顶点，透明遮罩只负责精确像素显示和淡红色边缘，不再承担机械断开职责。
- 成功提交的切口会保留淡红色边界；青色线只表示鼠标尚未松开时的临时轨迹。
- Game 视图左上角会显示当前工具和最近一次切割结果。看到 `Active Tool: CUT` 才表示已经切换到工具 `4`。
- 切回工具 `1` 后，可以从淡红色裂口两侧进行拉伸或翻折。
- `Open Cut Width` 控制开放切口宽度，默认 `0.04`。
- 点击 `Rebuild Mesh` 会重置已有切割。
- 闭合路径使用 Clipper2 清理和裁剪，再由 LibTessDotNet 三角化。

### 拖拽的正反面规则

- 抓住图片可见区域内部时，局部拉伸会受到限制；继续拖远也不会带动整块图片移动，并且不会因为三角形翻面而从完整表面露出背面。
- 只有抓住 Sprite 的真实可见轮廓附近时，网格才允许像掀开布角一样翻折并露出背面。
- `Deformable Image Interactor > Visible Edge Grab Distance` 控制多靠近轮廓才算边缘抓取，默认 `0.2`。
- `Maximum Interior Stretch` 控制内部抓取允许的最大局部拉伸，默认 `0.32`。超过该距离后形变停止增加。
- `Minimum Triangle Area Ratio` 防止内部三角形被压到接近零面积，通常保持默认 `0.02`。

## 图片导入设置

- Texture Type：Sprite (2D and UI)。
- Sprite Mode：Single。
- Wrap Mode：Clamp。
- Filter Mode：普通图片用 Bilinear，像素画用 Point。
- Read/Write：开启。拖拽工具会在创建网格时缓存正面图片的 Alpha，用它准确区分可见内部、真实边缘和透明区域。图片不可读时 Inspector 会显示一键修复按钮。

建议正反面图片尺寸、透明轮廓和长宽比一致。

## 当前限制

- 当前交互区域按规则网格判断，尚未用 Alpha 精确过滤透明区域。
- 当前膨胀是二维径向形变，不含立体光照。
- 开放切口会切断相交的网格连接；工具 `1` 使用沿完整网格边传播的最短路径距离，不再使用会直接跨过刀口的圆形直线距离。
- 内部切口先按完整网格计算绕行距离，再执行严格刀线可见性裁剪；从抓取点到顶点的直线只要与刀线相交，该顶点权重直接归零，因此不会因笔刷较大而从端点绕回刀口对面。
- 开放切割只处理刀线附近的局部像素区域，刀口边界和纹理也采用局部更新，以降低高清图片上的切割耗时。
- 尚未加入布料求解。

## 第一阶段：真实骨骼网格削减

1. 在 Project 中选中骨骼 Sprite。
2. 执行 `GameObject > 2D Object > Anatomy Bone Layer`。
3. 新对象会包含 `AnatomyBoneMesh` 与 `BoneCutInteractor`，默认 Sorting Order 为 `-10`。
4. 进入 Play，按 `5` 开关骨骼闭合切割工具。
5. 用左键画黄色闭合套索并让终点回到起点附近。松开后执行 Clipper2 Difference，保留骨骼和切除骨片都会由 LibTessDotNet 重新三角化。
6. Hierarchy 中新增 `Removed Bone Fragment`，可用左键整体移开。Inspector 的 Runtime Vertices / Triangles 可用于确认骨骼拓扑确实改变。
7. 当前阶段只完成骨骼真实切碎；下一阶段再加入 Jaw 权重纹理以及骨骼新轮廓对面部软组织的约束传递。

按 `5` 时面部工具会暂停接收鼠标，避免骨骼切割和面部拖拽同时发生。如果面部层遮挡骨骼，可暂时关闭面部对象的 Mesh Renderer 进行第一阶段验收。

## 第二阶段：自动 Jaw 权重驱动面部

1. 在 Hierarchy 中同时选中面部 `DeformableImageMesh` 对象和 `Anatomy Bone Layer`。
2. 执行 `GameObject > 2D Object > Connect Selected Face To Bone`。
3. 面部对象会新增 `BoneSkinCoupler2D`，并自动填写 Face Mesh 与 Jaw Bone 引用。
4. 进入 Play 后组件会依据骨骼 UV 的 Bottom / Peak / Top 纵向区间，以及面部顶点到原骨骼最大外轮廓的距离，生成自动 Jaw 权重。
5. 按 `5` 真实削减触及外轮廓的下颚骨时，新的外轮廓会通过权重和平滑迭代永久传递给面部；只在骨骼内部打孔不会改变最大外轮廓，因此不会错误地让皮肤塌陷。
6. 勾选 `Show Weight Gizmos` 并在 Scene 视图选中面部：蓝色表示低权重，红色表示高权重。可先调整 Jaw Region Bottom / Peak / Top 与 Influence Radius。
7. 按 `R` 会清除手动面部拖拽，但保留当前削骨形成的基础脸型。

这一阶段使用自动权重用于验证完整数据链。下一阶段将把权重持久化为 UV 灰度纹理并加入可视化权重笔刷。

## 第三方开源库

- `Assets/ThirdParty/Clipper2`：Boost Software License 1.0。
- `Assets/ThirdParty/LibTessDotNet`：SGI Free Software License B 2.0。
- 两个目录中均保留了原始许可证文件。
