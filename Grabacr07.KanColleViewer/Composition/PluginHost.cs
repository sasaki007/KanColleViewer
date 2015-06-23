﻿using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.ComponentModel.Composition.Hosting;
using System.ComponentModel.Composition.Primitives;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Grabacr07.KanColleViewer.Models;
using Grabacr07.KanColleWrapper;
using Livet;
using Livet.Behaviors;
using Livet.EventListeners;
using Livet.EventListeners.WeakEvents;

namespace Grabacr07.KanColleViewer.Composition
{
	/// <summary>
	/// プラグインを読み込み、機能へのアクセスを提供します。
	/// </summary>
	public class PluginHost : IDisposable
	{
		/// <summary>
		/// プラグインを検索するディレクトリへの相対パスを表す文字列を取得します。
		/// </summary>
		/// <value>プラグインを検索するディレクトリへの相対パスを表す文字列。</value>
		public const string PluginsDirectory = "Plugins";

		#region singleton members

		private static readonly PluginHost instance = new PluginHost();

		/// <summary>
		/// <see cref="PluginHost"/> のインスタンスを取得します。
		/// </summary>
		/// <value><see cref="PluginHost"/> のインスタンス。</value>
		public static PluginHost Instance
		{
			get { return instance; }
		}

		#endregion

		private CompositionContainer container;
		private InitializationResult initializationResult;
		private readonly List<Exception> unknownExceptions = new List<Exception>();
		private readonly LivetCompositeDisposable compositeDisposable = new LivetCompositeDisposable();

		/// <summary>
		/// プラグインの初期化処理 (<see cref="PluginHost.Initialize"/> メソッド) の結果を示す識別子を定義します。
		/// </summary>
		public enum InitializationResult
		{
			/// <summary>
			/// プラグインの初期化に成功しました。
			/// </summary>
			Succeeded,

			/// <summary>
			/// プラグインの初期化に失敗したため、アプリケーションを起動できません。
			/// </summary>
			Failed,

			/// <summary>
			/// プラグインの初期化に失敗したため、アプリケーションの再起動が必要です。
			/// </summary>
			RequiresRestart,
		}

		/// <summary>
		/// プラグインによって提供される通知機能を表すオブジェクトのシーケンスを取得します。
		/// </summary>
		[ImportMany]
		public IEnumerable<Lazy<INotifier, IPluginMetadata>> Notifiers { get; set; }

		/// <summary>
		/// プラグインによって提供されるツール機能を表すオブジェクトのシーケンスを取得します。
		/// </summary>
		[ImportMany]
		public IEnumerable<Lazy<IToolPlugin, IPluginMetadata>> Tools { get; set; }

		/// <summary>
		/// プラグインによって提供される拡張機能を表すオブジェクトのシーケンスを取得します。
		/// </summary>
		[ImportMany]
		public IEnumerable<Lazy<IPlugin, IPluginMetadata>> Plugins { get; set; }

		/// <summary>
		/// ロートされているすべてのプラグインのシーケンスを取得します。
		/// </summary>
		public IEnumerable<IPlugin> All
		{
			get
			{
				return this.Plugins.Select(x => x.Value)
					.Concat(this.Tools.Select(x => x.Value))
					.Concat(this.Notifiers.Select(x => x.Value));
			}
		}

		public IReadOnlyCollection<Exception> UnknownExceptions
		{
			get { return this.unknownExceptions; }
		}


		private PluginHost() { }


		/// <summary>
		/// プラグインをロードし、初期化を行います。
		/// </summary>
		public InitializationResult Initialize()
		{
			var currentDir = Path.GetDirectoryName(Assembly.GetCallingAssembly().Location);
			if (currentDir == null) return InitializationResult.Failed;

			var pluginsDir = Path.Combine(currentDir, PluginsDirectory);
			var ignores = Settings.Current.BlacklistedPlugins.ToArray();
			var catalog = new AggregateCatalog(new AssemblyCatalog(Assembly.GetExecutingAssembly()));
			var plugins = Directory.EnumerateFiles(pluginsDir, "*.dll", SearchOption.AllDirectories);

			Settings.Current.BlacklistedPlugins.Clear();

			foreach (var plugin in plugins)
			{
				// プラグインのパスのコレクションから、前回起動に失敗したプラグインを除外して
				// アセンブリ カタログとして結合していく

				var isIgnore = false;
				var filepath = plugin;
				foreach (var data in ignores.Where(x => string.Compare(x.FilePath, filepath, StringComparison.OrdinalIgnoreCase) == 0))
				{
					Settings.Current.BlacklistedPlugins.Add(data);
					isIgnore = true;
				}
				if (isIgnore) continue;

				try
				{
					var asmCatalog = new AssemblyCatalog(filepath);
					if (asmCatalog.Parts.ToList().Count > 0)
					{
						catalog.Catalogs.Add(asmCatalog);
					}
				}
				catch (ReflectionTypeLoadException ex)
				{
					Settings.Current.BlacklistedPlugins.Add(new BlacklistedPluginData
					{
						FilePath = filepath,
						Exception = ex.LoaderExceptions.Select(x => x.Message).ToString(Environment.NewLine),
					});
				}
				catch (BadImageFormatException ex)
				{
					Settings.Current.BlacklistedPlugins.Add(new BlacklistedPluginData
					{
						FilePath = filepath,
						Exception = ex.ToString(),
					});
				}
			}

			this.container = new CompositionContainer(catalog);
			this.container.ComposeParts(this);

			if (this.Check(this.Plugins) && this.Check(this.Tools) && this.Check(this.Notifiers))
			{
				// 起動成功 (全プラグイン、もしくはブラックリストのプラグインを除外してロードに成功) した状態
				// 普通に起動シークエンス続行

				Settings.Current.FailReboot = false;
				Settings.Current.Save();

				this.GetNotifier().Initialize();

				return this.initializationResult = InitializationResult.Succeeded;
			}

			if (Settings.Current.FailReboot)
			{
				// 前回起動に失敗して、今回も失敗した状態
				// おそらく、Check メソッド内で未知の例外が発生し BlacklistedPlugins に追加できないプラグインがある
				// 遺憾の意 (起動を諦める)

				Settings.Current.Save();

				return this.initializationResult = InitializationResult.Failed;
			}

			// 前回は起動に成功したと思われるが、今回は起動に失敗した状態
			// 起動に失敗したことをマークし、再起動を試みる

			Settings.Current.FailReboot = true;
			Settings.Current.Save();

			return this.initializationResult = InitializationResult.RequiresRestart;
		}

		public void Dispose()
		{
			this.container.Dispose();

			if (this.initializationResult == InitializationResult.Succeeded)
			{
				this.GetNotifier().Dispose();
			}
		}

		/// <summary>
		/// ロードされている全ての通知機能を集約して操作するオブジェクトを返します。
		/// </summary>
		/// <returns>ロードされている全ての通知機能を集約して操作するオブジェクト。</returns>
		public INotifier GetNotifier()
		{
			return new AggregateNotifier(this.Notifiers.Select(x => x.Value));
		}


		private bool Check<TPlugin>(IEnumerable<Lazy<TPlugin, IPluginMetadata>> plugins) where TPlugin : IPlugin
		{
			var success = true;
			IPluginMetadata metadata = null;

			foreach (var plugin in plugins)
			{
				try
				{
					// メソッドとか適当に呼び出してみるみるテスト
					// だいたい、.Value にアクセスしたときに死ぬ

					metadata = plugin.Metadata;

					var notifier = plugin.Value as IPlugin;
					if (notifier != null)
					{
						this.compositeDisposable.Add(new LivetWeakEventListener<EventHandler<NotifyEventArgs>, NotifyEventArgs>(
							x => x,
							x => notifier.NotifyRequested += x,
							x => notifier.NotifyRequested -= x,
							(sender, e) => NotifierHost.Instance.Notify(e.Title, e.Message, e.Activated, e.Failed)));
					}

				}
				catch (CompositionException ex)
				{
					foreach (var cause in ex.RootCauses)
					{
						var partException = cause as ComposablePartException;
						if (partException != null)
						{
							var asmCatalog = partException.Element.Origin as AssemblyCatalog;
							if (asmCatalog != null)
							{
								// AggregateCatalog に AssemblyCatalog しか Add してないんだから全部ここに来るはず…？

								var data = new BlacklistedPluginData
								{
									FilePath = asmCatalog.Assembly.Location,
									Exception = ex.Message,
								};

								if (metadata != null)
								{
									data.Metadata = new PluginMetadata
									{
										Author = metadata.Author,
										Description = metadata.Description,
										Title = metadata.Title,
										Version = metadata.Version,
									};
								}

								Settings.Current.BlacklistedPlugins.Add(data);
							}
						}
						else
						{
							// 他にプラグインのロード失敗時の例外パターンが判れば追加していきたく
							this.unknownExceptions.Add(cause);
						}
					}

					success = false;
				}
				catch (Exception ex)
				{
					this.unknownExceptions.Add(ex);
					success = false;
				}
			}

			return success;
		}
	}
}
