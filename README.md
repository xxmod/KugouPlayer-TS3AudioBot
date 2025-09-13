# KugouPlayer-TS3AudioBot

一个为 TS3AudioBot 开发的酷狗音乐插件，支持搜索、播放酷狗音乐，并提供便捷的音乐队列管理功能。

## 功能特性

* 🎵 酷狗音乐搜索与播放
* 🔍 智能搜索结果缓存
* 📱 二维码登录支持
* 🎶 播放队列管理
* 📝 用户歌单管理
* ⚡ 直接播放功能
* 🔐 用户登录状态保持

## 部署方法

##### 方法一

### 1\. 下载文件

* 从 [Releases](https://github.com/xxmod/KugouPlayer-TS3AudioBot/releases) 下载 `TS3AudioBot\\\_KugouPlayer.dll` 文件
* 从 [KuGouMusicApi](https://github.com/MakcRe/KuGouMusicApi/releases) 下载音乐 API 服务

### 2\. 安装插件

1. 将 `TS3AudioBot\\\_KugouPlayer.dll` 放入 TS3AudioBot 的 `plugins` 文件夹中
2. 启动 KuGouMusicApi 服务（默认端口 3000）
3. 在 TS3AudioBot 配置文件中添加权限设置

### 3\. 配置权限

在 `rights.toml` 中添加以下权限：

```toml
cmd.kugou.search
cmd.kugou.play  
cmd.kugou.dplay
cmd.kugou.add
cmd.kugou.login
cmd.kugou.list
cmd.kugou.playlist
cmd.kugou.vip
```

### 4\. 激活插件

在 TeamSpeak 聊天中使用命令：

```
!plugin load KugouPlayerPlugin
```



##### 部署方法二



### 1\.下载文件

* 从[relese](https://github.com/xxmod/KugouPlayer-TS3AudioBot/releases)下载KugouPluginwithTS3Bot.zip
* 从[KuGouMusicApi](https://github.com/MakcRe/KuGouMusicApi/releases)下载API



### 2.解压打开TS3Audiobot

这个版本已经内置插件并配置好了right.toml

解压后打开TS3AudioBot.exe连接至服务器即可使用



## 使用方法

### 登录账号

```
!kugou login
```

* 机器人会显示二维码（通过头像）
* 使用酷狗 APP 扫码登录
* 登录状态会自动保存

```
!kugou vip
```
* 与login方法一致
* 但是会在播放时优先使用vip的cookie
* 达到可以用带vip的账号播放自己的音乐

### 搜索音乐

```
!kugou search <关键词>
```

* 搜索相关歌曲，显示前 10 个结果
* 每个结果都有对应的序号
* 搜索结果会缓存给当前用户

### 播放音乐

#### 方式一：搜索后播放

```
!kugou play <序号>
```

* 播放搜索结果中的指定歌曲
* 不指定序号则播放第一首
* 需要先执行搜索命令

#### 方式二：直接播放

```
!kugou dplay <歌曲名>
```

* 搜索并直接播放第一首匹配的歌曲
* 无需先搜索，一步到位

### 添加到队列

```
!kugou add <歌曲名>
```

* 搜索并将第一首匹配的歌曲添加到播放队列的下一首位置

### 歌单管理

#### 获取歌单列表

```
!kugou list
```

* 显示已登录用户的所有歌单
* 显示歌单名称和歌曲数量
* 每个歌单都有对应的序号

#### 播放歌单

```
!kugou playlist <序号> [播放方式]
```

* 播放指定序号的歌单
* 不指定序号则播放第一个歌单
* 会将整个歌单添加到播放队列
* 需要先执行 `!kugou list` 命令获取歌单列表
* 播放方式为1即随机播放，0即顺序播放

## 技术说明

* **开发语言**: C# (.NET Core 3.1)
* **依赖框架**: TS3AudioBot Plugin API
* **音乐源**: 酷狗音乐 API
* **登录方式**: 二维码扫码登录
* **缓存机制**: 基于用户 UID 的搜索结果缓存

## 注意事项

* 需要配合 KuGouMusicApi 服务使用
* 登录状态会保存在用户数据目录
* API 服务默认运行在 `http://localhost:3000`
* 建议先登录账号以获得更好的音质和搜索结果
