﻿/* Этот файл является частью библиотеки Saraff.Twain.NET
 * © SARAFF SOFTWARE (Кирножицкий Андрей), 2011.
 * Saraff.Twain.NET - свободная программа: вы можете перераспространять ее и/или
 * изменять ее на условиях Меньшей Стандартной общественной лицензии GNU в том виде,
 * в каком она была опубликована Фондом свободного программного обеспечения;
 * либо версии 3 лицензии, либо (по вашему выбору) любой более поздней
 * версии.
 * Saraff.Twain.NET распространяется в надежде, что она будет полезной,
 * но БЕЗО ВСЯКИХ ГАРАНТИЙ; даже без неявной гарантии ТОВАРНОГО ВИДА
 * или ПРИГОДНОСТИ ДЛЯ ОПРЕДЕЛЕННЫХ ЦЕЛЕЙ. Подробнее см. в Меньшей Стандартной
 * общественной лицензии GNU.
 * Вы должны были получить копию Меньшей Стандартной общественной лицензии GNU
 * вместе с этой программой. Если это не так, см.
 * <http://www.gnu.org/licenses/>.)
 * 
 * This file is part of Saraff.Twain.NET.
 * © SARAFF SOFTWARE (Kirnazhytski Andrei), 2011.
 * Saraff.Twain.NET is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 * Saraff.Twain.NET is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU Lesser General Public License for more details.
 * You should have received a copy of the GNU Lesser General Public License
 * along with Saraff.Twain.NET. If not, see <http://www.gnu.org/licenses/>.
 * 
 * PLEASE SEND EMAIL TO:  twain@saraff.ru.
 */
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text;
using System.Runtime.InteropServices;
using System.ComponentModel;
using System.Reflection;
using System.Drawing;
using System.Diagnostics;
using System.IO;

namespace Saraff.Twain {

    /// <summary>
    /// Обеспечивает возможность работы с TWAIN-источниками.
    /// </summary>
    [DebuggerDisplay("ProductName = {_appid.ProductName.Value}, Version = {_appid.Version.Info}, DS = {_srcds.ProductName}")]
    [DefaultEvent("AcquireCompleted")]
    [DefaultProperty("AppProductName")]
    public sealed class TwainX : Component {
        private _DsmEntry _dsmEntry;
        private IntPtr _hTwainDll; //дескриптор модуля twain_32.dll
        private IContainer _components = new Container();
        private TwIdentity _appid; //идентификатор приложения.
        private TwIdentity _srcds; //идентификатор текущего источника данных.
        private TwIdentity[] _sources = new TwIdentity[0]; //массив доступных источников данных.
        private TwainStateFlag _twainState;
        private CallBackProc _callbackProc;
        private TwainCapabilities _capabilities;

        /// <summary>
        /// Initializes a new instance of the <see cref="TwainX"/> class.
        /// </summary>
        public TwainX() {
            this._srcds = new TwIdentity {
                Id = 0
            };
            this.ShowUI = false;
            this.DisableAfterAcquire = true;
            this.Palette = new TwainPalette(this);
            this._callbackProc = this._TwCallbackProc;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="TwainX"/> class.
        /// </summary>
        /// <param name="container">The container.</param>
        public TwainX(IContainer container) : this() {
            container.Add(this);
        }

        /// <summary>
        /// Releases the unmanaged resources used by the <see cref="T:System.ComponentModel.Component"/> and optionally releases the managed resources.
        /// </summary>
        /// <param name="disposing">true to release both managed and unmanaged resources; false to release only unmanaged resources.</param>
        protected override void Dispose(bool disposing) {
            if(disposing) {
                this.CloseDSM();
                this._UnloadDSM();
                if(this._components != null) {
                    this._components.Dispose();
                }
            }
            base.Dispose(disposing);
        }

        /// <summary>
        /// Открывает менеджер источников данных.
        /// </summary>
        /// <returns>Истина, если операция прошла удачно; иначе, лож.</returns>
        public bool OpenDSM() {
            if((this._TwainState & TwainStateFlag.DSMOpen) == 0) {

                #region Загружаем DSM, получаем адрес точки входа DSM_Entry и приводим ее к соответствующим делегатам

                switch(Environment.OSVersion.Platform) {
                    case PlatformID.Unix:
                    case PlatformID.MacOSX:
                        this._dsmEntry = _DsmEntry.Create(IntPtr.Zero);
                        try {
                            if(this._dsmEntry.DsmRaw == null) {
                                throw new InvalidOperationException("Cann't load DSM.");
                            }
                        } catch(Exception ex) {
                            throw new TwainException("Cann't load DSM.", ex);
                        }
                        break;
                    default:
                        this._hTwainDll = _Platform.Load(Path.ChangeExtension(Path.Combine(Environment.SystemDirectory, "TWAINDSM"), ".dll"));
                        if(this._hTwainDll != IntPtr.Zero) {
                            IntPtr _pDsmEntry = _Platform.GetProcAddr(this._hTwainDll, "DSM_Entry");
                            if(_pDsmEntry != IntPtr.Zero) {
                                this._dsmEntry = _DsmEntry.Create(_pDsmEntry);
                                _Memory._SetEntryPoints(null);
                            } else {
                                throw new TwainException("Cann't find DSM_Entry entry point.");
                            }
                        } else {
                            throw new TwainException("Cann't load DSM.");
                        }
                        break;
                }


                #endregion

                var _hwnd = IntPtr.Zero;
                for(TwRC _rc = this._dsmEntry.DsmParent(this._AppId, IntPtr.Zero, TwDG.Control, TwDAT.Parent, TwMSG.OpenDSM, ref _hwnd); _rc != TwRC.Success;) {
                    throw new TwainException(this._GetTwainStatus(), _rc);
                }
                this._TwainState |= TwainStateFlag.DSMOpen;

                if(this.IsTwain2Supported) {
                    TwEntryPoint _entry = new TwEntryPoint();
                    for(TwRC _rc = this._dsmEntry.DsmInvoke(this._AppId, TwDG.Control, TwDAT.EntryPoint, TwMSG.Get, ref _entry); _rc != TwRC.Success;) {
                        throw new TwainException(this._GetTwainStatus(), _rc);
                    }
                    _Memory._SetEntryPoints(_entry);
                } else {
                    throw new TwainException("DSM don't supported TWAIN 2.x specification.");
                }

                this._GetAllSorces();
            }
            return (this._TwainState & TwainStateFlag.DSMOpen) != 0;
        }

        /// <summary>
        /// Отображает диалоговое окно для выбора источника данных.
        /// </summary>
        /// <returns>Истина, если операция прошла удачно; иначе, лож.</returns>
        public bool SelectSource() {
            if(Environment.OSVersion.Platform == PlatformID.Unix) {
                throw new NotSupportedException("DG_CONTROL / DAT_IDENTITY / MSG_USERSELECT is not available on Linux.");
            }
            if((this._TwainState & TwainStateFlag.DSOpen) == 0) {
                if((this._TwainState & TwainStateFlag.DSMOpen) == 0) {
                    this.OpenDSM();
                    if((this._TwainState & TwainStateFlag.DSMOpen) == 0) {
                        return false;
                    }
                }
                TwIdentity _src = new TwIdentity();
                for(TwRC _rc = this._dsmEntry.DsmInvoke(this._AppId, TwDG.Control, TwDAT.Identity, TwMSG.UserSelect, ref _src); _rc != TwRC.Success;) {
                    if(_rc == TwRC.Cancel) {
                        return false;
                    }
                    throw new TwainException(this._GetTwainStatus(), _rc);
                }
                this._srcds = _src;
                return true;
            }
            return false;
        }

        /// <summary>
        /// Открывает источник данных.
        /// </summary>
        /// <returns>Истина, если операция прошла удачно; иначе, лож.</returns>
        public bool OpenDataSource() {
            if((this._TwainState & TwainStateFlag.DSMOpen) != 0 && (this._TwainState & TwainStateFlag.DSOpen) == 0) {
                for(TwRC _rc = this._dsmEntry.DsmInvoke(this._AppId, TwDG.Control, TwDAT.Identity, TwMSG.OpenDS, ref this._srcds); _rc != TwRC.Success;) {
                    throw new TwainException(this._GetTwainStatus(), _rc);
                }
                this._TwainState |= TwainStateFlag.DSOpen;

                switch(Environment.OSVersion.Platform) {
                    case PlatformID.Unix:
                    case PlatformID.MacOSX:
                        this._RegisterCallback();
                        break;
                    default:
                        if((this._srcds.SupportedGroups & TwDG.DS2) != 0) {
                            this._RegisterCallback();
                        } else {
                            throw new TwainException("DS don't supported TWAIN 2.x specification.");
                        }
                        break;
                }

            }
            return (this._TwainState & TwainStateFlag.DSOpen) != 0;
        }

        /// <summary>
        /// Регестрирует обработчик событий источника данных.
        /// </summary>
        private void _RegisterCallback() {
            TwCallback2 _callback = new TwCallback2 {
                CallBackProc = this._callbackProc
            };
            TwRC _rc = this._dsmEntry.DsInvoke(this._AppId, this._srcds, TwDG.Control, TwDAT.Callback2, TwMSG.RegisterCallback, ref _callback);
            if(_rc != TwRC.Success) {
                throw new TwainException(this._GetTwainStatus(), _rc);
            }
        }

        /// <summary>
        /// Активирует источник данных.
        /// </summary>
        /// <returns>Истина, если операция прошла удачно; иначе, лож.</returns>
        private bool _EnableDataSource() {
            if((this._TwainState & TwainStateFlag.DSOpen) != 0 && (this._TwainState & TwainStateFlag.DSEnabled) == 0) {
                TwUserInterface _guif = new TwUserInterface() {
                    ShowUI = this.ShowUI,
                    ModalUI = this.ModalUI,
                    ParentHand = IntPtr.Zero
                };
                for(TwRC _rc = this._dsmEntry.DsInvoke(this._AppId, this._srcds, TwDG.Control, TwDAT.UserInterface, TwMSG.EnableDS, ref _guif); _rc != TwRC.Success;) {
                    throw new TwainException(this._GetTwainStatus(), _rc);
                }
                if((this._TwainState & TwainStateFlag.DSReady) != 0) {
                    this._TwainState &= ~TwainStateFlag.DSReady;
                } else {
                    this._TwainState |= TwainStateFlag.DSEnabled;
                }
            }
            return (this._TwainState & TwainStateFlag.DSEnabled) != 0;
        }

        /// <summary>
        /// Получает изображение с источника данных.
        /// </summary>
        public void Acquire() {
            if(this.OpenDSM()) {
                if(this.OpenDataSource()) {
                    if(this._EnableDataSource()) {
                        switch(Environment.OSVersion.Platform) {
                            case PlatformID.Unix:
                            case PlatformID.MacOSX:
                                break;
                            default:
                                if((this._srcds.SupportedGroups & TwDG.DS2) == 0) {
                                    throw new TwainException("DS don't supported TWAIN 2.x specification.");
                                }
                                break;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Деактивирует источник данных.
        /// </summary>
        /// <returns>Истина, если операция прошла удачно; иначе, лож.</returns>
        private bool _DisableDataSource() {
            if((this._TwainState & TwainStateFlag.DSEnabled) != 0) {
                try {
                    TwUserInterface _guif = new TwUserInterface() {
                        ParentHand = IntPtr.Zero,
                        ShowUI = false
                    };
                    for(TwRC _rc = this._dsmEntry.DsInvoke(this._AppId, this._srcds, TwDG.Control, TwDAT.UserInterface, TwMSG.DisableDS, ref _guif); _rc != TwRC.Success;) {
                        throw new TwainException(this._GetTwainStatus(), _rc);
                    }
                } finally {
                    this._TwainState &= ~TwainStateFlag.DSEnabled;
                }
                return (this._TwainState & TwainStateFlag.DSEnabled) == 0;
            }
            return false;
        }

        /// <summary>
        /// Закрывает источник данных.
        /// </summary>
        /// <returns>Истина, если операция прошла удачно; иначе, лож.</returns>
        public bool CloseDataSource() {
            if((this._TwainState & TwainStateFlag.DSOpen) != 0 && (this._TwainState & TwainStateFlag.DSEnabled) == 0) {
                for(TwRC _rc = this._dsmEntry.DsmInvoke(this._AppId, TwDG.Control, TwDAT.Identity, TwMSG.CloseDS, ref this._srcds); _rc != TwRC.Success;) {
                    throw new TwainException(this._GetTwainStatus(), _rc);
                }
                this._TwainState &= ~TwainStateFlag.DSOpen;
                return (this._TwainState & TwainStateFlag.DSOpen) == 0;
            }
            return false;
        }

        /// <summary>
        /// Закрывает менежер источников данных.
        /// </summary>
        /// <returns>Истина, если операция прошла удачно; иначе, лож.</returns>
        public bool CloseDSM() {
            if((this._TwainState & TwainStateFlag.DSEnabled) != 0) {
                this._DisableDataSource();
            }
            if((this._TwainState & TwainStateFlag.DSOpen) != 0) {
                this.CloseDataSource();
            }
            if((this._TwainState & TwainStateFlag.DSMOpen) != 0 && (this._TwainState & TwainStateFlag.DSOpen) == 0) {
                var _hwnd = IntPtr.Zero;
                for(TwRC _rc = this._dsmEntry.DsmParent(this._AppId, IntPtr.Zero, TwDG.Control, TwDAT.Parent, TwMSG.CloseDSM, ref _hwnd); _rc != TwRC.Success;) {
                    throw new TwainException(this._GetTwainStatus(), _rc);
                }
                this._TwainState &= ~TwainStateFlag.DSMOpen;
                this._UnloadDSM();
                return (this._TwainState & TwainStateFlag.DSMOpen) == 0;
            }
            return false;
        }

        private void _UnloadDSM() {
            this._AppId = null;
            if(this._hTwainDll != IntPtr.Zero) {
                _Platform.Unload(this._hTwainDll);
                this._hTwainDll = IntPtr.Zero;
            }
        }

        /// <summary>
        /// Возвращает или устанавливает значение, указывающее на необходимость деактивации источника данных после получения изображения.
        /// </summary>
        [DefaultValue(true)]
        [Category("Behavior")]
        [Description("Возвращает или устанавливает значение, указывающее на необходимость деактивации источника данных после получения изображения.")]
        public bool DisableAfterAcquire { get; set; }

        /// <summary>
        /// Возвращает истину, если DSM поддерживает TWAIN 2.0; иначе лож.
        /// </summary>
        [Browsable(false)]
        private bool IsTwain2Supported {
            get {
                if((this._TwainState & TwainStateFlag.DSMOpen) == 0) {
                    throw new InvalidOperationException("DSM is not open.");
                }
                return (this._AppId.SupportedGroups & TwDG.DSM2) != 0;
            }
        }

        #region Information of sorces

        /// <summary>
        /// Возвращает или устанавливает индекс текущего источника данных.
        /// </summary>
        [Browsable(false)]
        [ReadOnly(true)]
        public int SourceIndex {
            get {
                if((this._TwainState & TwainStateFlag.DSMOpen) != 0) {
                    int i;
                    for(i = 0; i < this._sources.Length; i++) {
                        if(this._sources[i].Equals(this._srcds)) {
                            break;
                        }
                    }
                    return i;
                } else {
                    return -1;
                }
            }
            set {
                if((this._TwainState & TwainStateFlag.DSMOpen) != 0) {
                    if((this._TwainState & TwainStateFlag.DSOpen) == 0) {
                        this._srcds = this._sources[value];
                    } else {
                        throw new TwainException("Источник данных уже открыт.");
                    }
                } else {
                    throw new TwainException("Менеджер источников данных не открыт.");
                }
            }
        }

        /// <summary>
        /// Возвращает количество источников данных.
        /// </summary>
        [Browsable(false)]
        public int SourcesCount => this._sources.Length;

        /// <summary>
        /// Возвращает имя источника данных по указанному индексу.
        /// </summary>
        /// <param name="index">Индекс.</param>
        /// <returns>Имя источника данных.</returns>
        public string GetSourceProductName(int index) => this._sources[index].ProductName;

        /// <summary>
        /// Возвращает описание указанного источника. Gets the source identity.
        /// </summary>
        /// <param name="index">Индекс. The index.</param>
        /// <returns>Описание источника данных.</returns>
        public Identity GetSourceIdentity(int index) => new Identity(this._sources[index]);

        /// <summary>
        /// Возвращает истину, если указанный источник поддерживает TWAIN 2.0; иначе лож.
        /// </summary>
        /// <param name="index">Индекс.</param>
        /// <returns>Истина, если указанный источник поддерживает TWAIN 2.0; иначе лож.</returns>
        public bool GetIsSourceTwain2Compatible(int index) => (this._sources[index].SupportedGroups & TwDG.DS2) != 0;

        /// <summary>
        /// Устанавливает указанный источник данных в качестве источника данных по умолчанию.
        /// </summary>
        /// <param name="index">Индекс.</param>
        public void SetDefaultSource(int index) {
            if((this._TwainState & TwainStateFlag.DSMOpen) != 0) {
                if((this._TwainState & TwainStateFlag.DSOpen) == 0) {
                    TwIdentity _src = this._sources[index];
                    TwRC _rc = this._dsmEntry.DsmInvoke(this._AppId, TwDG.Control, TwDAT.Identity, TwMSG.Set, ref _src);
                    if(_rc != TwRC.Success) {
                        throw new TwainException(this._GetTwainStatus(), _rc);
                    }
                } else {
                    throw new TwainException("Источник данных уже открыт. Необходимо сперва закрыть источник данных.");
                }
            } else {
                throw new TwainException("DSM не открыт.");
            }
        }

        /// <summary>
        /// Gets the default Data Source.
        /// </summary>
        /// <returns>Index of default Data Source.</returns>
        /// <exception cref="TwainException">
        /// Не удалось найти источник данных по умолчанию.
        /// or
        /// DSM не открыт.
        /// </exception>
        public int GetDefaultSource() {
            if((this._TwainState & TwainStateFlag.DSMOpen) != 0) {
                TwIdentity _identity = new TwIdentity();
                for(TwRC _rc = this._dsmEntry.DsmInvoke(this._AppId, TwDG.Control, TwDAT.Identity, TwMSG.GetDefault, ref _identity); _rc != TwRC.Success;) {
                    throw new TwainException(this._GetTwainStatus(), _rc);
                }
                for(var i = 0; i < this._sources.Length; i++) {
                    if(_identity.Id == this._sources[i].Id) {
                        return i;
                    }
                }
                throw new TwainException("Не удалось найти источник данных по умолчанию.");
            } else {
                throw new TwainException("DSM не открыт.");
            }
        }

        #endregion

        #region Properties of source

        /// <summary>
        /// Возвращает идентификатор приложения.
        /// </summary>
        [Browsable(false)]
        [ReadOnly(true)]
        private TwIdentity _AppId {
            get {
                if(this._appid == null) {
                    Assembly _asm = typeof(TwainX).Assembly;
                    Version _version = new Version((Attribute.GetCustomAttribute(_asm, typeof(AssemblyFileVersionAttribute), false) as AssemblyFileVersionAttribute)?.Version ?? "1.0.0.0");

                    this._appid = new TwIdentity() {
                        Id = 0,
                        Version = new TwVersion() {
                            MajorNum = (ushort)_version.Major,
                            MinorNum = (ushort)_version.Minor,
                            Language = TwLanguage.RUSSIAN,
                            Country = TwCountry.BELARUS,
                            Info = new AssemblyName(_asm.FullName).Version.ToString()
                        },
                        ProtocolMajor = 2,
                        ProtocolMinor = 4,
                        SupportedGroups = TwDG.Image | TwDG.Control | TwDG.APP2,
                        Manufacturer = (Attribute.GetCustomAttribute(_asm, typeof(AssemblyCompanyAttribute), false) as AssemblyCompanyAttribute)?.Company ?? "SARAFF SOFTWARE",
                        ProductFamily = "TWAIN Class Library",
                        ProductName = (Attribute.GetCustomAttribute(_asm, typeof(AssemblyProductAttribute), false) as AssemblyProductAttribute)?.Product ?? "Saraff.Twain.NET"
                    };
                }
                return this._appid;
            }
            set {
                if(value != null) {
                    throw new ArgumentException("Is read only property.");
                }
                this._appid = null;
            }
        }

        /// <summary>
        /// Возвращает или устанавливает имя приложения.
        /// </summary>
        [Category("Behavior")]
        [Description("Возвращает или устанавливает имя приложения.")]
        public string AppProductName {
            get {
                return this._AppId.ProductName;
            }
            set {
                this._AppId.ProductName = value;
            }
        }

        /// <summary>
        /// Возвращает или устанавливает значение указывающие на необходимость отображения UI TWAIN-источника.
        /// </summary>
        [Category("Behavior")]
        [DefaultValue(false)]
        [Description("Возвращает или устанавливает значение указывающие на необходимость отображения UI TWAIN-источника.")]
        public bool ShowUI { get; set; }

        [Category("Behavior")]
        [DefaultValue(false)]
        private bool ModalUI { get; set; }

        /// <summary>
        /// Возвращает или устанавливает используемый приложением язык. Get or set the primary language for your application.
        /// </summary>
        [Category("Culture")]
        [DefaultValue(TwLanguage.RUSSIAN)]
        [Description("Возвращает или устанавливает используемый приложением язык. Get or set the primary language for your application.")]
        public TwLanguage Language {
            get {
                return this._AppId.Version.Language;
            }
            set {
                this._AppId.Version.Language = value;
            }
        }

        /// <summary>
        /// Возвращает или устанавливает страну происхождения приложения. Get or set the primary country where your application is intended to be distributed.
        /// </summary>
        [Category("Culture")]
        [DefaultValue(TwCountry.BELARUS)]
        [Description("Возвращает или устанавливает страну происхождения приложения. Get or set the primary country where your application is intended to be distributed.")]
        public TwCountry Country {
            get {
                return this._AppId.Version.Country;
            }
            set {
                this._AppId.Version.Country = value;
            }
        }

        /// <summary>
        /// Возвращает или устанавливает кадр физического расположения изображения.
        /// </summary>
        [Browsable(false)]
        [ReadOnly(true)]
        public RectangleF ImageLayout {
            get {
                TwImageLayout _imageLayout = new TwImageLayout();
                TwRC _rc = this._dsmEntry.DsInvoke(this._AppId, this._srcds, TwDG.Image, TwDAT.ImageLayout, TwMSG.Get, ref _imageLayout);
                if(_rc != TwRC.Success) {
                    throw new TwainException(this._GetTwainStatus(), _rc);
                }
                return _imageLayout.Frame;
            }
            set {
                TwImageLayout _imageLayout = new TwImageLayout { Frame = value };
                TwRC _rc = this._dsmEntry.DsInvoke(this._AppId, this._srcds, TwDG.Image, TwDAT.ImageLayout, TwMSG.Set, ref _imageLayout);
                if(_rc != TwRC.Success) {
                    throw new TwainException(this._GetTwainStatus(), _rc);
                }
            }
        }

        /// <summary>
        /// Возвращает набор возможностей (Capabilities).
        /// </summary>
        [Browsable(false)]
        [ReadOnly(true)]
        public TwainCapabilities Capabilities {
            get {
                if(this._capabilities == null) {
                    this._capabilities = new TwainCapabilities(this);
                }
                return this._capabilities;
            }
        }

        /// <summary>
        /// Возвращает набор операций для работы с цветовой палитрой.
        /// </summary>
        [Browsable(false)]
        [ReadOnly(true)]
        public TwainPalette Palette { get; private set; }

        #endregion

        #region All capabilities

        /// <summary>
        /// Возвращает флаги, указывающие на поддерживаемые источником данных операции, для указанного значения capability.
        /// </summary>
        /// <param name="capability">Значение перечисдения TwCap.</param>
        /// <returns>Набор флагов.</returns>
        /// <exception cref="TwainException">Возбуждается в случае возникновения ошибки во время операции.</exception>
        public TwQC IsCapSupported(TwCap capability) {
            if((this._TwainState & TwainStateFlag.DSOpen) != 0) {
                TwCapability _cap = new TwCapability(capability);
                try {
                    TwRC _rc = this._dsmEntry.DsInvoke(this._AppId, this._srcds, TwDG.Control, TwDAT.Capability, TwMSG.QuerySupport, ref _cap);
                    if(_rc == TwRC.Success) {
                        return (TwQC)((TwOneValue)_cap.GetValue()).Item;
                    }
                    return 0;
                } finally {
                    _cap.Dispose();
                }
            } else {
                throw new TwainException("Источник данных не открыт.");
            }
        }

        /// <summary>
        /// Возвращает значение для указанного capability (возможность).
        /// </summary>
        /// <param name="capability">Значение перечисления TwCap.</param>
        /// <param name="msg">Значение перечисления TwMSG.</param>
        /// <returns>В зависимости от значение capability, могут быть возвращены: тип-значение, массив, <see cref="TwainX.Range">диапазон</see>, <see cref="TwainX.Enumeration">перечисление</see>.</returns>
        /// <exception cref="TwainException">Возбуждается в случае возникновения ошибки во время операции.</exception>
        private object _GetCapCore(TwCap capability, TwMSG msg) {
            if((this._TwainState & TwainStateFlag.DSOpen) != 0) {
                TwCapability _cap = new TwCapability(capability);
                try {
                    TwRC _rc = this._dsmEntry.DsInvoke(this._AppId, this._srcds, TwDG.Control, TwDAT.Capability, msg, ref _cap);
                    if(_rc == TwRC.Success) {
                        switch(_cap.ConType) {
                            case TwOn.One:
                                object _valueRaw = _cap.GetValue();
                                TwOneValue _value = _valueRaw as TwOneValue;
                                if(_value != null) {
                                    return TwTypeHelper.CastToCommon(_value.ItemType, TwTypeHelper.ValueToTw<uint>(_value.ItemType, _value.Item));
                                } else {
                                    return _valueRaw;
                                }
                            case TwOn.Range:
                                return Range.CreateRange((TwRange)_cap.GetValue());
                            case TwOn.Array:
                                return ((__ITwArray)_cap.GetValue()).Items;
                            case TwOn.Enum:
                                __ITwEnumeration _enum = _cap.GetValue() as __ITwEnumeration;
                                return Enumeration.CreateEnumeration(_enum.Items, _enum.CurrentIndex, _enum.DefaultIndex);
                        }
                        return _cap.GetValue();
                    } else {
                        throw new TwainException(this._GetTwainStatus(), _rc);
                    }
                } finally {
                    _cap.Dispose();
                }
            } else {
                throw new TwainException("Источник данных не открыт.");
            }
        }

        /// <summary>
        /// Возвращает значения указанной возможности (capability).
        /// </summary>
        /// <param name="capability">Значение перечисления TwCap.</param>
        /// <returns>В зависимости от значение capability, могут быть возвращены: тип-значение, массив, <see cref="TwainX.Range">диапазон</see>, <see cref="TwainX.Enumeration">перечисление</see>.</returns>
        /// <exception cref="TwainException">Возбуждается в случае возникновения ошибки во время операции.</exception>
        public object GetCap(TwCap capability) => this._GetCapCore(capability, TwMSG.Get);

        /// <summary>
        /// Возвращает текущее значение для указанной возможности (capability).
        /// </summary>
        /// <param name="capability">Значение перечисления TwCap.</param>
        /// <returns>В зависимости от значение capability, могут быть возвращены: тип-значение, массив, <see cref="TwainX.Range">диапазон</see>, <see cref="TwainX.Enumeration">перечисление</see>.</returns>
        /// <exception cref="TwainException">Возбуждается в случае возникновения ошибки во время операции.</exception>
        public object GetCurrentCap(TwCap capability) => this._GetCapCore(capability, TwMSG.GetCurrent);

        /// <summary>
        /// Возвращает значение по умолчанию для указанной возможности (capability).
        /// </summary>
        /// <param name="capability">Значение перечисления TwCap.</param>
        /// <returns>В зависимости от значение capability, могут быть возвращены: тип-значение, массив, <see cref="TwainX.Range">диапазон</see>, <see cref="TwainX.Enumeration">перечисление</see>.</returns>
        /// <exception cref="TwainException">Возбуждается в случае возникновения ошибки во время операции.</exception>
        public object GetDefaultCap(TwCap capability) => this._GetCapCore(capability, TwMSG.GetDefault);

        /// <summary>
        /// Сбрасывает текущее значение для указанного <see cref="TwCap">capability</see> в значение по умолчанию.
        /// </summary>
        /// <param name="capability">Значение перечисления <see cref="TwCap"/>.</param>
        /// <exception cref="TwainException">Возбуждается в случае возникновения ошибки во время операции.</exception>
        public void ResetCap(TwCap capability) {
            if((this._TwainState & TwainStateFlag.DSOpen) != 0) {
                TwCapability _cap = new TwCapability(capability);
                try {
                    TwRC _rc = this._dsmEntry.DsInvoke(this._AppId, this._srcds, TwDG.Control, TwDAT.Capability, TwMSG.Reset, ref _cap);
                    if(_rc != TwRC.Success) {
                        throw new TwainException(this._GetTwainStatus(), _rc);
                    }
                } finally {
                    _cap.Dispose();
                }
            } else {
                throw new TwainException("Источник данных не открыт.");
            }
        }

        /// <summary>
        /// Сбрасывает текущее значение всех текущих значений в значения по умолчанию.
        /// </summary>
        /// <exception cref="TwainException">Возбуждается в случае возникновения ошибки во время операции.</exception>
        public void ResetAllCap() {
            if((this._TwainState & TwainStateFlag.DSOpen) != 0) {
                TwCapability _cap = new TwCapability(TwCap.SupportedCaps);
                try {
                    TwRC _rc = this._dsmEntry.DsInvoke(this._AppId, this._srcds, TwDG.Control, TwDAT.Capability, TwMSG.ResetAll, ref _cap);
                    if(_rc != TwRC.Success) {
                        throw new TwainException(this._GetTwainStatus(), _rc);
                    }
                } finally {
                    _cap.Dispose();
                }
            } else {
                throw new TwainException("Источник данных не открыт.");
            }
        }

        private void _SetCapCore(TwCapability cap, TwMSG msg) {
            if((this._TwainState & TwainStateFlag.DSOpen) != 0) {
                try {
                    TwRC _rc = this._dsmEntry.DsInvoke(this._AppId, this._srcds, TwDG.Control, TwDAT.Capability, msg, ref cap);
                    if(_rc != TwRC.Success) {
                        throw new TwainException(this._GetTwainStatus(), _rc);
                    }
                } finally {
                    cap.Dispose();
                }
            } else {
                throw new TwainException("Источник данных не открыт.");
            }
        }

        private void _SetCapCore(TwCap capability, TwMSG msg, object value) {
            TwCapability _cap = null;
            if(value is string) {
                object[] _attrs = typeof(TwCap).GetField(capability.ToString()).GetCustomAttributes(typeof(TwTypeAttribute), false);
                if(_attrs?.Length > 0) {
                    _cap = new TwCapability(capability, (string)value, ((TwTypeAttribute)_attrs[0]).TwType);
                } else {
                    _cap = new TwCapability(capability, (string)value, TwTypeHelper.TypeOf(value));
                }
            } else {
                TwType _type = TwTypeHelper.TypeOf(value.GetType());
                _cap = new TwCapability(capability, TwTypeHelper.ValueFromTw<uint>(TwTypeHelper.CastToTw(_type, value)), _type);
            }
            this._SetCapCore(_cap, msg);
        }

        private void _SetCapCore(TwCap capability, TwMSG msg, object[] value) {
            var _attrs = typeof(TwCap).GetField(capability.ToString())?.GetCustomAttributes(typeof(TwTypeAttribute), false);
            this._SetCapCore(
                new TwCapability(
                    capability,
                    new TwArray() {
                        ItemType = _attrs?.Length > 0 ? ((TwTypeAttribute)(_attrs[0])).TwType : TwTypeHelper.TypeOf(value[0]),
                        NumItems = (uint)value.Length
                    },
                    value),
                msg);
        }

        private void _SetCapCore(TwCap capability, TwMSG msg, Range value) => this._SetCapCore(new TwCapability(capability, value.ToTwRange()), msg);

        private void _SetCapCore(TwCap capability, TwMSG msg, Enumeration value) {
            var _attrs = typeof(TwCap).GetField(capability.ToString())?.GetCustomAttributes(typeof(TwTypeAttribute), false);
            this._SetCapCore(
                new TwCapability(
                    capability,
                    new TwEnumeration {
                        ItemType = _attrs?.Length > 0 ? ((TwTypeAttribute)(_attrs[0])).TwType : TwTypeHelper.TypeOf(value[0]),
                        NumItems = (uint)value.Count,
                        CurrentIndex = (uint)value.CurrentIndex,
                        DefaultIndex = (uint)value.DefaultIndex
                    },
                    value.Items),
                msg);
        }

        /// <summary>
        /// Устанавливает значение для указанного <see cref="TwCap">capability</see>
        /// </summary>
        /// <param name="capability">Значение перечисления <see cref="TwCap"/>.</param>
        /// <param name="value">Устанавливаемое значение.</param>
        /// <exception cref="TwainException">Возникает в случае, если источник данных не открыт.</exception>
        public void SetCap(TwCap capability, object value) => this._SetCapCore(capability, TwMSG.Set, value);

        /// <summary>
        /// Устанавливает значение для указанного <see cref="TwCap">capability</see>
        /// </summary>
        /// <param name="capability">Значение перечисления <see cref="TwCap"/>.</param>
        /// <param name="value">Устанавливаемое значение.</param>
        /// <exception cref="TwainException">Возникает в случае, если источник данных не открыт.</exception>
        public void SetCap(TwCap capability, object[] value) => this._SetCapCore(capability, TwMSG.Set, value);

        /// <summary>
        /// Устанавливает значение для указанного <see cref="TwCap">capability</see>
        /// </summary>
        /// <param name="capability">Значение перечисления <see cref="TwCap"/>.</param>
        /// <param name="value">Устанавливаемое значение.</param>
        /// <exception cref="TwainException">Возникает в случае, если источник данных не открыт.</exception>
        public void SetCap(TwCap capability, Range value) => this._SetCapCore(capability, TwMSG.Set, value);

        /// <summary>
        /// Устанавливает значение для указанного <see cref="TwCap">capability</see>
        /// </summary>
        /// <param name="capability">Значение перечисления <see cref="TwCap"/>.</param>
        /// <param name="value">Устанавливаемое значение.</param>
        /// <exception cref="TwainException">Возникает в случае, если источник данных не открыт.</exception>
        public void SetCap(TwCap capability, Enumeration value) => this._SetCapCore(capability, TwMSG.Set, value);

        /// <summary>
        /// Устанавливает ограничение на значения указанной возможности.
        /// </summary>
        /// <param name="capability">Значение перечисления <see cref="TwCap"/>.</param>
        /// <param name="value">Устанавливаемое значение.</param>
        /// <exception cref="TwainException">Возникает в случае, если источник данных не открыт.</exception>
        public void SetConstraintCap(TwCap capability, object value) => this._SetCapCore(capability, TwMSG.SetConstraint, value);

        /// <summary>
        /// Устанавливает ограничение на значения указанной возможности.
        /// </summary>
        /// <param name="capability">Значение перечисления <see cref="TwCap"/>.</param>
        /// <param name="value">Устанавливаемое значение.</param>
        /// <exception cref="TwainException">Возникает в случае, если источник данных не открыт.</exception>
        public void SetConstraintCap(TwCap capability, object[] value) => this._SetCapCore(capability, TwMSG.SetConstraint, value);

        /// <summary>
        /// Устанавливает ограничение на значения указанной возможности.
        /// </summary>
        /// <param name="capability">Значение перечисления <see cref="TwCap"/>.</param>
        /// <param name="value">Устанавливаемое значение.</param>
        /// <exception cref="TwainException">Возникает в случае, если источник данных не открыт.</exception>
        public void SetConstraintCap(TwCap capability, Range value) => this._SetCapCore(capability, TwMSG.SetConstraint, value);

        /// <summary>
        /// Устанавливает ограничение на значения указанной возможности.
        /// </summary>
        /// <param name="capability">Значение перечисления <see cref="TwCap"/>.</param>
        /// <param name="value">Устанавливаемое значение.</param>
        /// <exception cref="TwainException">Возникает в случае, если источник данных не открыт.</exception>
        public void SetConstraintCap(TwCap capability, Enumeration value) => this._SetCapCore(capability, TwMSG.SetConstraint, value);

        #endregion

        #region DG_IMAGE / IMAGExxxxXFER / MSG_GET operation

        /// <summary>
        /// Выполняет передачу изображения (Native Mode Transfer).
        /// </summary>
        private void _NativeTransferPictures() {
            if(this._srcds.Id == 0) {
                return;
            }
            IntPtr _hBitmap = IntPtr.Zero;
            TwPendingXfers _pxfr = new TwPendingXfers();
            try {
                do {
                    _pxfr.Count = 0;
                    _hBitmap = IntPtr.Zero;

                    for(TwRC _rc = this._dsmEntry.DSImageXfer(this._AppId, this._srcds, TwDG.Image, TwDAT.ImageNativeXfer, TwMSG.Get, ref _hBitmap); _rc != TwRC.XferDone;) {
                        throw new TwainException(this._GetTwainStatus(), _rc);
                    }
                    // DG_IMAGE / DAT_IMAGEINFO / MSG_GET
                    // DG_IMAGE / DAT_EXTIMAGEINFO / MSG_GET
                    if(this._OnXferDone(new XferDoneEventArgs(this._GetImageInfo, this._GetExtImageInfo))) {
                        return;
                    }

                    IntPtr _pBitmap = _Memory.Lock(_hBitmap);
                    try {

                        IImageHandler _handler = this.GetService(typeof(IImageHandler)) as IImageHandler;
                        if(_handler == null) {
                            switch(Environment.OSVersion.Platform) {
                                case PlatformID.Unix:
                                    _handler = new Tiff();
                                    break;
                                case PlatformID.MacOSX:
                                    _handler = new Pict();
                                    break;
                                default:
                                    _handler = new DibToImage();
                                    break;
                            }
                        }
                        _Image _img = _handler.PtrToStream(_pBitmap, this.GetService(typeof(IStreamProvider)) as IStreamProvider);

                        if(this._OnEndXfer(new EndXferEventArgs(_img))) {
                            return;
                        }
                    } finally {
                        _Memory.Unlock(_hBitmap);
                        _Memory.Free(_hBitmap);
                    }
                    for(TwRC _rc = this._dsmEntry.DsInvoke(this._AppId, this._srcds, TwDG.Control, TwDAT.PendingXfers, TwMSG.EndXfer, ref _pxfr); _rc != TwRC.Success;) {
                        throw new TwainException(this._GetTwainStatus(), _rc);
                    }
                } while(_pxfr.Count != 0);
            } finally {
                TwRC _rc = this._dsmEntry.DsInvoke(this._AppId, this._srcds, TwDG.Control, TwDAT.PendingXfers, TwMSG.Reset, ref _pxfr);
            }
        }

        /// <summary>
        /// Выполняет передачу изображения (Disk File Mode Transfer).
        /// </summary>
        private void _FileTransferPictures() {
            if(this._srcds.Id == 0) {
                return;
            }

            TwPendingXfers _pxfr = new TwPendingXfers();
            try {
                do {
                    _pxfr.Count = 0;

                    SetupFileXferEventArgs _args = new SetupFileXferEventArgs();
                    if(this._OnSetupFileXfer(_args)) {
                        return;
                    }

                    TwSetupFileXfer _fileXfer = new TwSetupFileXfer {
                        Format = this.Capabilities.ImageFileFormat.IsSupported(TwQC.GetCurrent) ? this.Capabilities.ImageFileFormat.GetCurrent() : TwFF.Bmp,
                        FileName = string.IsNullOrEmpty(_args.FileName) ? Path.GetTempFileName() : _args.FileName
                    };

                    for(TwRC _rc = this._dsmEntry.DsInvoke(this._AppId, this._srcds, TwDG.Control, TwDAT.SetupFileXfer, TwMSG.Set, ref _fileXfer); _rc != TwRC.Success;) {
                        throw new TwainException(this._GetTwainStatus(), _rc);
                    }

                    for(TwRC _rc = this._dsmEntry.DsRaw(this._AppId, this._srcds, TwDG.Image, TwDAT.ImageFileXfer, TwMSG.Get, IntPtr.Zero); _rc != TwRC.XferDone;) {
                        throw new TwainException(this._GetTwainStatus(), _rc);
                    }
                    // DG_IMAGE / DAT_IMAGEINFO / MSG_GET
                    // DG_IMAGE / DAT_EXTIMAGEINFO / MSG_GET
                    if(this._OnXferDone(new XferDoneEventArgs(this._GetImageInfo, this._GetExtImageInfo))) {
                        return;
                    }

                    for(TwRC _rc = this._dsmEntry.DsInvoke(this._AppId, this._srcds, TwDG.Control, TwDAT.PendingXfers, TwMSG.EndXfer, ref _pxfr); _rc != TwRC.Success;) {
                        throw new TwainException(this._GetTwainStatus(), _rc);
                    }
                    for(TwRC _rc = this._dsmEntry.DsInvoke(this._AppId, this._srcds, TwDG.Control, TwDAT.SetupFileXfer, TwMSG.Get, ref _fileXfer); _rc != TwRC.Success;) {
                        throw new TwainException(this._GetTwainStatus(), _rc);
                    }
                    if(this._OnFileXfer(new FileXferEventArgs(ImageFileXfer.Create(_fileXfer)))) {
                        return;
                    }
                } while(_pxfr.Count != 0);
            } finally {
                TwRC _rc = this._dsmEntry.DsInvoke(this._AppId, this._srcds, TwDG.Control, TwDAT.PendingXfers, TwMSG.Reset, ref _pxfr);
            }
        }

        /// <summary>
        /// Выполняет передачу изображения (Buffered Memory Mode Transfer and Memory File Mode Transfer).
        /// </summary>
        private void _MemoryTransferPictures(bool isMemFile) {
            if(this._srcds.Id == 0) {
                return;
            }

            TwPendingXfers _pxfr = new TwPendingXfers();
            try {
                do {
                    _pxfr.Count = 0;
                    ImageInfo _info = this._GetImageInfo();

                    if(isMemFile) {
                        if((this.Capabilities.ImageFileFormat.IsSupported() & TwQC.GetCurrent) != 0) {
                            TwSetupFileXfer _fileXfer = new TwSetupFileXfer {
                                Format = this.Capabilities.ImageFileFormat.GetCurrent()
                            };
                            for(TwRC _rc = this._dsmEntry.DsInvoke(this._AppId, this._srcds, TwDG.Control, TwDAT.SetupFileXfer, TwMSG.Set, ref _fileXfer); _rc != TwRC.Success;) {
                                throw new TwainException(this._GetTwainStatus(), _rc);
                            }
                        }
                    }

                    TwSetupMemXfer _memBufSize = new TwSetupMemXfer();

                    for(TwRC _rc = this._dsmEntry.DsInvoke(this._AppId, this._srcds, TwDG.Control, TwDAT.SetupMemXfer, TwMSG.Get, ref _memBufSize); _rc != TwRC.Success;) {
                        throw new TwainException(this._GetTwainStatus(), _rc);
                    }
                    if(this._OnSetupMemXfer(new SetupMemXferEventArgs(_info, _memBufSize.Preferred))) {
                        return;
                    }

                    IntPtr _hMem = _Memory.Alloc((int)_memBufSize.Preferred);
                    if(_hMem == IntPtr.Zero) {
                        throw new TwainException("Ошибка выделениия памяти.");
                    }
                    try {
                        TwMemory _mem = new TwMemory {
                            Flags = TwMF.AppOwns | TwMF.Pointer,
                            Length = _memBufSize.Preferred,
                            TheMem = _Memory.Lock(_hMem)
                        };

                        do {
                            TwImageMemXfer _memXferBuf = new TwImageMemXfer { Memory = _mem };
                            _Memory.ZeroMemory(_memXferBuf.Memory.TheMem, (IntPtr)_memXferBuf.Memory.Length);

                            TwRC _rc = this._dsmEntry.DsInvoke(this._AppId, this._srcds, TwDG.Image, isMemFile ? TwDAT.ImageMemFileXfer : TwDAT.ImageMemXfer, TwMSG.Get, ref _memXferBuf);
                            if(_rc != TwRC.Success && _rc != TwRC.XferDone) {
                                TwCC _cc = this._GetTwainStatus();
                                TwRC _rc2 = this._dsmEntry.DsInvoke(this._AppId, this._srcds, TwDG.Control, TwDAT.PendingXfers, TwMSG.EndXfer, ref _pxfr);
                                throw new TwainException(_cc, _rc);
                            }
                            if(this._OnMemXfer(new MemXferEventArgs(_info, ImageMemXfer.Create(_memXferBuf)))) {
                                return;
                            }
                            if(_rc == TwRC.XferDone) {
                                // DG_IMAGE / DAT_IMAGEINFO / MSG_GET
                                // DG_IMAGE / DAT_EXTIMAGEINFO / MSG_GET
                                if(this._OnXferDone(new XferDoneEventArgs(this._GetImageInfo, this._GetExtImageInfo))) {
                                    return;
                                }
                                break;
                            }
                        } while(true);
                    } finally {
                        _Memory.Unlock(_hMem);
                        _Memory.Free(_hMem);
                    }
                    for(TwRC _rc = this._dsmEntry.DsInvoke(this._AppId, this._srcds, TwDG.Control, TwDAT.PendingXfers, TwMSG.EndXfer, ref _pxfr); _rc != TwRC.Success;) {
                        throw new TwainException(this._GetTwainStatus(), _rc);
                    }
                } while(_pxfr.Count != 0);
            } finally {
                TwRC _rc = this._dsmEntry.DsInvoke(this._AppId, this._srcds, TwDG.Control, TwDAT.PendingXfers, TwMSG.Reset, ref _pxfr);
            }
        }

        #endregion

        #region DG_CONTROL / DAT_CUSTOMDSDATA / MSG_xxx

        /// <summary>
        /// Get or set the Custom Data (DG_CONTROL / DAT_CUSTOMDSDATA / MSG_xxx).
        /// </summary>
        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        public byte[] CustomDSData {
            get {
                if((this._TwainState & TwainStateFlag.DSOpen) != 0) {
                    TwCustomDSData _data = new TwCustomDSData { };
                    TwRC _rc = this._dsmEntry.DsInvoke(this._AppId, this._srcds, TwDG.Control, TwDAT.CustomDSData, TwMSG.Get, ref _data);
                    if(_rc != TwRC.Success) {
                        throw new TwainException(this._GetTwainStatus(), _rc);
                    }
                    byte[] _value = new byte[_data.InfoLength];
                    Marshal.Copy(_Memory.Lock(_data.hData), _value, 0, _value.Length);
                    _Memory.Unlock(_data.hData);
                    _Memory.Free(_data.hData);
                    return _value;
                } else {
                    throw new TwainException("Источник данных не открыт.");
                }
            }
            set {
                if((this._TwainState & TwainStateFlag.DSOpen) != 0) {
                    TwCustomDSData _data = new TwCustomDSData {
                        InfoLength = (uint)value.Length,
                        hData = _Memory.Alloc(value.Length)
                    };
                    Marshal.Copy(value, 0, _Memory.Lock(_data.hData), value.Length);
                    _Memory.Unlock(_data.hData);
                    TwRC _rc = this._dsmEntry.DsInvoke(this._AppId, this._srcds, TwDG.Control, TwDAT.CustomDSData, TwMSG.Set, ref _data);
                    if(_rc != TwRC.Success) {
                        throw new TwainException(this._GetTwainStatus(), _rc);
                    }
                    _Memory.Free(_data.hData);
                } else {
                    throw new TwainException("Источник данных не открыт.");
                }
            }
        }

        #endregion

        #region DS events handler

        /// <summary>
        /// Обработчик событий источника данных.
        /// </summary>
        /// <param name="appId">Описание приложения.</param>
        /// <param name="srcId">Описание источника данных.</param>
        /// <param name="dg">Описание группы данных.</param>
        /// <param name="dat">Описание данных.</param>
        /// <param name="msg">Сообщение.</param>
        /// <param name="data">Данные.</param>
        /// <returns>Результат обработники события.</returns>
        private TwRC _TwCallbackProc(TwIdentity srcId, TwIdentity appId, TwDG dg, TwDAT dat, TwMSG msg, IntPtr data) {
            try {
                if(appId == null || appId.Id != this._AppId.Id) {
                    return TwRC.Failure;
                }

                if((this._TwainState & TwainStateFlag.DSEnabled) == 0) {
                    this._TwainState |= TwainStateFlag.DSEnabled | TwainStateFlag.DSReady;
                }

                this._TwCallbackProcCore(msg, isCloseReq => {
                    if(isCloseReq || this.DisableAfterAcquire) {
                        this._DisableDataSource();
                    }
                });
            } catch(Exception ex) {
                this._OnAcquireError(new AcquireErrorEventArgs(new TwainException(ex.Message, ex)));
            }
            return TwRC.Success;
        }

        /// <summary>
        /// Внутренний обработчик событий источника данных.
        /// </summary>
        /// <param name="msg">Сообщение.</param>
        /// <param name="endAction">Действие, завершающее обработку события.</param>
        private void _TwCallbackProcCore(TwMSG msg, Action<bool> endAction) {
            try {
                switch(msg) {
                    case TwMSG.XFerReady:
                        switch(this.Capabilities.XferMech.GetCurrent()) {
                            case TwSX.File:
                                this._FileTransferPictures();
                                break;
                            case TwSX.Memory:
                                this._MemoryTransferPictures(false);
                                break;
                            case TwSX.MemFile:
                                this._MemoryTransferPictures(true);
                                break;
                            default:
                                this._NativeTransferPictures();
                                break;
                        }
                        endAction(false);
                        this._OnAcquireCompleted(new EventArgs());
                        break;
                    case TwMSG.CloseDSReq:
                        endAction(true);
                        break;
                    case TwMSG.CloseDSOK:
                        endAction(false);
                        break;
                    case TwMSG.DeviceEvent:
                        this._DeviceEventObtain();
                        break;
                }
            } catch(TwainException ex) {
                try {
                    endAction(false);
                } catch {
                }
                this._OnAcquireError(new AcquireErrorEventArgs(ex));
            } catch {
                try {
                    endAction(false);
                } catch {
                }
                throw;
            }
        }

        private void _DeviceEventObtain() {
            TwDeviceEvent _deviceEvent = new TwDeviceEvent();
            if(this._dsmEntry.DsInvoke(this._AppId, this._srcds, TwDG.Control, TwDAT.DeviceEvent, TwMSG.Get, ref _deviceEvent) == TwRC.Success) {
                this._OnDeviceEvent(new DeviceEventEventArgs(_deviceEvent));
            }
        }

        #endregion

        #region Raise events

        private void _OnAcquireCompleted(EventArgs e) => this.AcquireCompleted?.Invoke(this, e);

        private void _OnAcquireError(AcquireErrorEventArgs e) => this.AcquireError?.Invoke(this, e);

        private bool _OnXferDone(XferDoneEventArgs e) {
            this.XferDone?.Invoke(this, e);
            return e.Cancel;
        }

        private bool _OnEndXfer(EndXferEventArgs e) {
            this.EndXfer?.Invoke(this, e);
            return e.Cancel;
        }

        private bool _OnSetupMemXfer(SetupMemXferEventArgs e) {
            this.SetupMemXferEvent?.Invoke(this, e);
            return e.Cancel;
        }

        private bool _OnMemXfer(MemXferEventArgs e) {
            this.MemXferEvent?.Invoke(this, e);
            return e.Cancel;
        }

        private bool _OnSetupFileXfer(SetupFileXferEventArgs e) {
            this.SetupFileXferEvent?.Invoke(this, e);
            return e.Cancel;
        }

        private bool _OnFileXfer(FileXferEventArgs e) {
            this.FileXferEvent?.Invoke(this, e);
            return e.Cancel;
        }

        private void _OnDeviceEvent(DeviceEventEventArgs e) => this.DeviceEvent?.Invoke(this, e);

        #endregion

        /// <summary>
        /// Получает описание всех доступных источников данных.
        /// </summary>
        private void _GetAllSorces() {
            List<TwIdentity> _src = new List<TwIdentity>();
            TwIdentity _item = new TwIdentity();
            try {
                for(TwRC _rc = this._dsmEntry.DsmInvoke(this._AppId, TwDG.Control, TwDAT.Identity, TwMSG.GetFirst, ref _item); _rc != TwRC.Success;) {
                    if(_rc == TwRC.EndOfList) {
                        return;
                    }
                    throw new TwainException(this._GetTwainStatus(), _rc);
                }
                _src.Add(_item);
                while(true) {
                    _item = new TwIdentity();
                    TwRC _rc = this._dsmEntry.DsmInvoke(this._AppId, TwDG.Control, TwDAT.Identity, TwMSG.GetNext, ref _item);
                    if(_rc == TwRC.Success) {
                        _src.Add(_item);
                        continue;
                    }
                    if(_rc == TwRC.EndOfList) {
                        break;
                    }
                    throw new TwainException(this._GetTwainStatus(), _rc);
                }
                for(TwRC _rc = this._dsmEntry.DsmInvoke(this._AppId, TwDG.Control, TwDAT.Identity, TwMSG.GetDefault, ref _srcds); _rc != TwRC.Success;) {
                    throw new TwainException(this._GetTwainStatus(), _rc);
                }
            } finally {
                this._sources = _src.ToArray();
            }
        }

        /// <summary>
        /// Возвращает или устанавливает значение флагов состояния.
        /// </summary>
        private TwainStateFlag _TwainState {
            get {
                return this._twainState;
            }
            set {
                if(this._twainState != value) {
                    this._twainState = value;
                    this.TwainStateChanged?.Invoke(this, new TwainStateEventArgs(this._twainState));
                }
            }
        }

        /// <summary>
        /// Возвращает код состояния TWAIN.
        /// </summary>
        /// <returns></returns>
        private TwCC _GetTwainStatus() {
            TwStatus _status = new TwStatus();
            _ = this._dsmEntry.DsInvoke(this._AppId, this._srcds, TwDG.Control, TwDAT.Status, TwMSG.Get, ref _status);
            return _status.ConditionCode;
        }

        /// <summary>
        /// Возвращает описание полученного изображения.
        /// </summary>
        /// <returns>Описание изображения.</returns>
        private ImageInfo _GetImageInfo() {
            TwImageInfo _imageInfo = new TwImageInfo();
            TwRC _rc = this._dsmEntry.DsInvoke(this._AppId, this._srcds, TwDG.Image, TwDAT.ImageInfo, TwMSG.Get, ref _imageInfo);
            if(_rc != TwRC.Success) {
                throw new TwainException(this._GetTwainStatus(), _rc);
            }
            return ImageInfo.FromTwImageInfo(_imageInfo);
        }

        /// <summary>
        /// Возвращает расширенного описание полученного изображения.
        /// </summary>
        /// <param name="extInfo">Набор кодов расширенного описания изображения для которых требуется получить описание.</param>
        /// <returns>Расширенное описание изображения.</returns>
        private ExtImageInfo _GetExtImageInfo(TwEI[] extInfo) {
            TwInfo[] _info = new TwInfo[extInfo.Length];
            for(int i = 0; i < extInfo.Length; i++) {
                _info[i] = new TwInfo { InfoId = extInfo[i] };
            }
            IntPtr _extImageInfo = TwExtImageInfo.ToPtr(_info);
            try {
                TwRC _rc = this._dsmEntry.DsRaw(this._AppId, this._srcds, TwDG.Image, TwDAT.ExtImageInfo, TwMSG.Get, _extImageInfo);
                if(_rc != TwRC.Success) {
                    throw new TwainException(this._GetTwainStatus(), _rc);
                }
                return ExtImageInfo.FromPtr(_extImageInfo);
            } finally {
                Marshal.FreeHGlobal(_extImageInfo);
            }
        }

        /// <summary>
        /// Флаги состояния.
        /// </summary>
        [Flags]
        public enum TwainStateFlag {

            /// <summary>
            /// The DSM open.
            /// </summary>
            DSMOpen = 0x1,

            /// <summary>
            /// The ds open.
            /// </summary>
            DSOpen = 0x2,

            /// <summary>
            /// The ds enabled.
            /// </summary>
            DSEnabled = 0x4,

            /// <summary>
            /// The ds ready.
            /// </summary>
            DSReady = 0x08
        }

        #region Events

        /// <summary>
        /// Возникает в момент окончания сканирования. Occurs when the acquire is completed.
        /// </summary>
        [Category("Action")]
        [Description("Возникает в момент окончания сканирования. Occurs when the acquire is completed.")]
        public event EventHandler AcquireCompleted;

        /// <summary>
        /// Возникает в момент получения ошибки в процессе сканирования. Occurs when error received during acquire.
        /// </summary>
        [Category("Action")]
        [Description("Возникает в момент получения ошибки в процессе сканирования. Occurs when error received during acquire.")]
        public event EventHandler<AcquireErrorEventArgs> AcquireError;

        /// <summary>
        /// Возникает в момент окончания получения изображения приложением. Occurs when the transfer into application was completed (Native Mode Transfer).
        /// </summary>
        [Category("Native Mode Action")]
        [Description("Возникает в момент окончания получения изображения приложением. Occurs when the transfer into application was completed (Native Mode Transfer).")]
        public event EventHandler<EndXferEventArgs> EndXfer;

        /// <summary>
        /// Возникает в момент окончания получения изображения источником.
        /// </summary>
        [Category("Action")]
        [Description("Возникает в момент окончания получения изображения источником. Occurs when the transfer was completed.")]
        public event EventHandler<XferDoneEventArgs> XferDone;

        /// <summary>
        /// Возникает в момент установки размера буфера памяти. Occurs when determined size of buffer to use during the transfer (Memory Mode Transfer and MemFile Mode Transfer).
        /// </summary>
        [Category("Memory Mode Action")]
        [Description("Возникает в момент установки размера буфера памяти. Occurs when determined size of buffer to use during the transfer (Memory Mode Transfer and MemFile Mode Transfer).")]
        public event EventHandler<SetupMemXferEventArgs> SetupMemXferEvent;

        /// <summary>
        /// Возникает в момент получения очередного блока данных. Occurs when the memory block for the data was recived (Memory Mode Transfer and MemFile Mode Transfer).
        /// </summary>
        [Category("Memory Mode Action")]
        [Description("Возникает в момент получения очередного блока данных. Occurs when the memory block for the data was recived (Memory Mode Transfer and MemFile Mode Transfer).")]
        public event EventHandler<MemXferEventArgs> MemXferEvent;

        /// <summary>
        /// Возникает в момент, когда необходимо задать имя файла изображения. Occurs when you need to specify the filename (File Mode Transfer).
        /// </summary>
        [Category("File Mode Action")]
        [Description("Возникает в момент, когда необходимо задать имя файла изображения. Occurs when you need to specify the filename. (File Mode Transfer)")]
        public event EventHandler<SetupFileXferEventArgs> SetupFileXferEvent;

        /// <summary>
        /// Возникает в момент окончания получения файла изображения приложением. Occurs when the transfer into application was completed (File Mode Transfer).
        /// </summary>
        [Category("File Mode Action")]
        [Description("Возникает в момент окончания получения файла изображения приложением. Occurs when the transfer into application was completed (File Mode Transfer).")]
        public event EventHandler<FileXferEventArgs> FileXferEvent;

        /// <summary>
        /// Возникает в момент изменения состояния twain-устройства. Occurs when TWAIN state was changed.
        /// </summary>
        [Category("Behavior")]
        [Description("Возникает в момент изменения состояния twain-устройства. Occurs when TWAIN state was changed.")]
        public event EventHandler<TwainStateEventArgs> TwainStateChanged;

        /// <summary>
        /// Возникает в момент, когда источник уведомляет приложение о произошедшем событии. Occurs when enabled the source sends this message to the Application to alert it that some event has taken place.
        /// </summary>
        [Category("Behavior")]
        [Description("Возникает в момент, когда источник уведомляет приложение о произошедшем событии. Occurs when enabled the source sends this message to the Application to alert it that some event has taken place.")]
        public event EventHandler<DeviceEventEventArgs> DeviceEvent;

        #endregion

        #region Events Args

        /// <summary>
        /// Аргументы события EndXfer.
        /// </summary>
        [Serializable]
        public sealed class EndXferEventArgs : SerializableCancelEventArgs {
            private readonly _Image _image;

            /// <summary>
            /// Инициализирует новый экземпляр класса.
            /// </summary>
            /// <param name="image">Изображение.</param>
            internal EndXferEventArgs(object image) {
                this._image = image as _Image;
            }

            public T CreateImage<T>(IImageFactory<T> factory) where T : class => factory.Create(this._image);
        }

        /// <summary>
        /// Аргументы события XferDone.
        /// </summary>
        public sealed class XferDoneEventArgs : SerializableCancelEventArgs {
            private readonly GetImageInfoCallback _imageInfoMethod;
            private readonly GetExtImageInfoCallback _extImageInfoMethod;

            /// <summary>
            /// Инициализирует новый экземпляр класса <see cref="XferDoneEventArgs"/>.
            /// </summary>
            /// <param name="method1">Метод обратного вызова для получения описания изображения.</param>
            /// <param name="method2">Метод обратного вызова для получения расширенного описания изображения.</param>
            internal XferDoneEventArgs(GetImageInfoCallback method1, GetExtImageInfoCallback method2) {
                this._imageInfoMethod = method1;
                this._extImageInfoMethod = method2;
            }

            /// <summary>
            /// Возвращает описание полученного изображения.
            /// </summary>
            /// <returns>Описание изображения.</returns>
            public ImageInfo GetImageInfo() => this._imageInfoMethod();

            /// <summary>
            /// Возвращает расширенного описание полученного изображения.
            /// </summary>
            /// <param name="extInfo">Набор кодов расширенного описания изображения для которых требуется получить описание.</param>
            /// <returns>Расширенное описание изображения.</returns>
            public ExtImageInfo GetExtImageInfo(params TwEI[] extInfo) => this._extImageInfoMethod(extInfo);
        }

        /// <summary>
        /// Аргументы события SetupMemXferEvent.
        /// </summary>
        [Serializable]
        public sealed class SetupMemXferEventArgs : SerializableCancelEventArgs {

            /// <summary>
            /// Инициализирует новый экземпляр класса <see cref="SetupMemXferEventArgs"/>.
            /// </summary>
            /// <param name="info">Описание изображения.</param>
            /// <param name="bufferSize">Размер буфера памяти для передачи данных.</param>
            internal SetupMemXferEventArgs(ImageInfo info, uint bufferSize) {
                this.ImageInfo = info;
                this.BufferSize = bufferSize;
            }

            /// <summary>
            /// Возвращает описание изображения.
            /// </summary>
            public ImageInfo ImageInfo { get; private set; }

            /// <summary>
            /// Возвращает размер буфера памяти для передачи данных.
            /// </summary>
            public uint BufferSize { get; private set; }
        }

        /// <summary>
        /// Аргументы события MemXferEvent.
        /// </summary>
        [Serializable]
        public sealed class MemXferEventArgs : SerializableCancelEventArgs {

            /// <summary>
            /// Инициализирует новый экземпляр класса <see cref="MemXferEventArgs"/>.
            /// </summary>
            /// <param name="info">Описание изображения.</param>
            /// <param name="image">Фрагмент данных изображения.</param>
            internal MemXferEventArgs(ImageInfo info, ImageMemXfer image) {
                this.ImageInfo = info;
                this.ImageMemXfer = image;
            }

            /// <summary>
            /// Возвращает описание изображения.
            /// </summary>
            public ImageInfo ImageInfo { get; private set; }

            /// <summary>
            /// Возвращает фрагмент данных изображения.
            /// </summary>
            public ImageMemXfer ImageMemXfer { get; private set; }
        }

        /// <summary>
        /// Аргументы события SetupFileXferEvent.
        /// </summary>
        [Serializable]
        public sealed class SetupFileXferEventArgs : SerializableCancelEventArgs {

            /// <summary>
            /// Инициализирует новый экземпляр класса <see cref="SetupFileXferEventArgs"/>.
            /// </summary>
            internal SetupFileXferEventArgs() {
            }

            /// <summary>
            /// Возвращает или устанавливает имя файла изображения.
            /// </summary>
            public string FileName { get; set; }
        }

        /// <summary>
        /// Аргументы события FileXferEvent.
        /// </summary>
        [Serializable]
        public sealed class FileXferEventArgs : SerializableCancelEventArgs {

            /// <summary>
            /// Инициализирует новый экземпляр класса <see cref="FileXferEventArgs"/>.
            /// </summary>
            /// <param name="image">Описание файла изображения.</param>
            internal FileXferEventArgs(ImageFileXfer image) {
                this.ImageFileXfer = image;
            }

            /// <summary>
            /// Возвращает описание файла изображения.
            /// </summary>
            public ImageFileXfer ImageFileXfer { get; private set; }
        }

        /// <summary>
        /// Аргументы события TwainStateChanged.
        /// </summary>
        [Serializable]
        public sealed class TwainStateEventArgs : EventArgs {

            /// <summary>
            /// Инициализирует новый экземпляр класса.
            /// </summary>
            /// <param name="flags">Флаги состояния.</param>
            internal TwainStateEventArgs(TwainStateFlag flags) {
                this.TwainState = flags;
            }

            /// <summary>
            /// Возвращает флаги состояния twain-устройства.
            /// </summary>
            public TwainStateFlag TwainState { get; private set; }
        }

        /// <summary>
        /// Аргументы события DeviceEvent.
        /// </summary>
        public sealed class DeviceEventEventArgs : EventArgs {
            private readonly TwDeviceEvent _deviceEvent;

            internal DeviceEventEventArgs(TwDeviceEvent deviceEvent) {
                this._deviceEvent = deviceEvent;
            }

            /// <summary>
            /// One of the TWDE_xxxx values.
            /// </summary>
            public TwDE Event => this._deviceEvent.Event;

            /// <summary>
            /// The name of the device that generated the event.
            /// </summary>
            public string DeviceName => this._deviceEvent.DeviceName;

            /// <summary>
            /// Battery Minutes Remaining.
            /// </summary>
            public uint BatteryMinutes => this._deviceEvent.BatteryMinutes;

            /// <summary>
            /// Battery Percentage Remaining.
            /// </summary>
            public short BatteryPercentAge => this._deviceEvent.BatteryPercentAge;

            /// <summary>
            /// Power Supply.
            /// </summary>
            public int PowerSupply => this._deviceEvent.PowerSupply;

            /// <summary>
            /// Resolution.
            /// </summary>
            public float XResolution => this._deviceEvent.XResolution;

            /// <summary>
            /// Resolution.
            /// </summary>
            public float YResolution => this._deviceEvent.YResolution;

            /// <summary>
            /// Flash Used2.
            /// </summary>
            public uint FlashUsed2 => this._deviceEvent.FlashUsed2;

            /// <summary>
            /// Automatic Capture.
            /// </summary>
            public uint AutomaticCapture => this._deviceEvent.AutomaticCapture;

            /// <summary>
            /// Automatic Capture.
            /// </summary>
            public uint TimeBeforeFirstCapture => this._deviceEvent.TimeBeforeFirstCapture;

            /// <summary>
            /// Automatic Capture.
            /// </summary>
            public uint TimeBetweenCaptures => this._deviceEvent.TimeBetweenCaptures;
        }

        /// <summary>
        /// Аргументы события AcquireError.
        /// </summary>
        [Serializable]
        public sealed class AcquireErrorEventArgs : EventArgs {

            /// <summary>
            /// Инициализирует новый экземпляр класса.
            /// </summary>
            /// <param name="ex">Экземпляр класса исключения.</param>
            internal AcquireErrorEventArgs(TwainException ex) {
                this.Exception = ex;
            }

            /// <summary>
            /// Возвращает экземпляр класса исключения.
            /// </summary>
            public TwainException Exception { get; private set; }
        }

        /// <summary>
        /// 
        /// </summary>
        /// <seealso cref="System.EventArgs" />
        [Serializable]
        public class SerializableCancelEventArgs : EventArgs {

            /// <summary>
            /// Получает или задает значение, показывающее, следует ли отменить событие. Gets or sets a value indicating whether the event should be canceled.
            /// </summary>
            /// <value>
            /// Значение <c>true</c>, если событие следует отменить, в противном случае — значение <c>false</c>. <c>true</c> if cancel; otherwise, <c>false</c>.
            /// </value>
            public bool Cancel { get; set; }
        }

        #endregion

        #region Nested classes

        /// <summary>
        /// Точки входа для работы с DSM.
        /// </summary>
        private sealed class _DsmEntry {

            /// <summary>
            /// Инициализирует новый экземпляр класса <see cref="_DsmEntry"/>.
            /// </summary>
            /// <param name="ptr">Указатель на DSM_Entry.</param>
            private _DsmEntry(IntPtr ptr) {
                switch(Environment.OSVersion.Platform) {
                    case PlatformID.Unix:
                        this.DsmParent = _DsmEntry._LinuxDsmParent;
                        this.DsmRaw = _DsmEntry._LinuxDsmRaw;
                        this.DSImageXfer = _DsmEntry._LinuxDsImageXfer;
                        this.DsRaw = _DsmEntry._LinuxDsRaw;
                        break;
                    case PlatformID.MacOSX:
                        this.DsmParent = _DsmEntry._MacosxDsmParent;
                        this.DsmRaw = _DsmEntry._MacosxDsmRaw;
                        this.DSImageXfer = _DsmEntry._MacosxDsImageXfer;
                        this.DsRaw = _DsmEntry._MacosxDsRaw;
                        break;
                    default:
                        MethodInfo _createDelegate = typeof(_DsmEntry).GetMethod("CreateDelegate", BindingFlags.Static | BindingFlags.NonPublic);
                        foreach(PropertyInfo _prop in typeof(_DsmEntry).GetProperties()) {
                            _prop.SetValue(this, _createDelegate.MakeGenericMethod(_prop.PropertyType).Invoke(this, new object[] { ptr }), null);
                        }
                        break;
                }
            }

            /// <summary>
            /// Создает и возвращает новый экземпляр класса <see cref="_DsmEntry"/>.
            /// </summary>
            /// <param name="ptr">Указатель на DSM_Entry.</param>
            /// <returns>Экземпляр класса <see cref="_DsmEntry"/>.</returns>
            public static _DsmEntry Create(IntPtr ptr) => new _DsmEntry(ptr);

            /// <summary>
            /// Приводит указатель к требуемомы делегату.
            /// </summary>
            /// <typeparam name="T">Требуемый делегат.</typeparam>
            /// <param name="ptr">Указатель на DSM_Entry.</param>
            /// <returns>Делегат.</returns>
            private static T CreateDelegate<T>(IntPtr ptr) where T : class => Marshal.GetDelegateForFunctionPointer(ptr, typeof(T)) as T;

            public TwRC DsmInvoke<T>(TwIdentity origin, TwDG dg, TwDAT dat, TwMSG msg, ref T data) where T : class {
                if(data == null) {
                    throw new ArgumentNullException();
                }
                IntPtr _data = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(T)));
                try {
                    Marshal.StructureToPtr(data, _data, true);

                    TwRC _rc = this.DsmRaw(origin, IntPtr.Zero, dg, dat, msg, _data);
                    if(_rc == TwRC.Success) {
                        data = (T)Marshal.PtrToStructure(_data, typeof(T));
                    }
                    return _rc;
                } finally {
                    Marshal.FreeHGlobal(_data);
                }
            }

            public TwRC DsInvoke<T>(TwIdentity origin, TwIdentity dest, TwDG dg, TwDAT dat, TwMSG msg, ref T data) where T : class {
                if(data == null) {
                    throw new ArgumentNullException();
                }
                IntPtr _data = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(T)));
                try {
                    Marshal.StructureToPtr(data, _data, true);

                    TwRC _rc = this.DsRaw(origin, dest, dg, dat, msg, _data);
                    if(_rc == TwRC.Success || _rc == TwRC.DSEvent || _rc == TwRC.XferDone) {
                        data = (T)Marshal.PtrToStructure(_data, typeof(T));
                    }
                    return _rc;
                } finally {
                    Marshal.FreeHGlobal(_data);
                }
            }

            #region Properties

            public _DSMparent DsmParent { get; private set; }

            public _DSMraw DsmRaw { get; private set; }

            public _DSixfer DSImageXfer { get; private set; }

            public _DSraw DsRaw { get; private set; }

            #endregion

            #region import libtwaindsm.so (Unix)

            [DllImport("/usr/local/lib/libtwaindsm.so", EntryPoint = "DSM_Entry", CharSet = CharSet.Ansi)]
            private static extern TwRC _LinuxDsmParent([In, Out] TwIdentity origin, IntPtr zeroptr, TwDG dg, TwDAT dat, TwMSG msg, ref IntPtr refptr);

            [DllImport("/usr/local/lib/libtwaindsm.so", EntryPoint = "DSM_Entry", CharSet = CharSet.Ansi)]
            private static extern TwRC _LinuxDsmRaw([In, Out] TwIdentity origin, IntPtr zeroptr, TwDG dg, TwDAT dat, TwMSG msg, IntPtr rawData);

            [DllImport("/usr/local/lib/libtwaindsm.so", EntryPoint = "DSM_Entry", CharSet = CharSet.Ansi)]
            private static extern TwRC _LinuxDsImageXfer([In, Out] TwIdentity origin, [In, Out] TwIdentity dest, TwDG dg, TwDAT dat, TwMSG msg, ref IntPtr hbitmap);

            [DllImport("/usr/local/lib/libtwaindsm.so", EntryPoint = "DSM_Entry", CharSet = CharSet.Ansi)]
            private static extern TwRC _LinuxDsRaw([In, Out] TwIdentity origin, [In, Out] TwIdentity dest, TwDG dg, TwDAT dat, TwMSG msg, IntPtr arg);

            #endregion

            #region import TWAIN.framework/TWAIN (MacOSX)

            [DllImport("/System/Library/Frameworks/TWAIN.framework/TWAIN", EntryPoint = "DSM_Entry", CharSet = CharSet.Ansi)]
            private static extern TwRC _MacosxDsmParent([In, Out] TwIdentity origin, IntPtr zeroptr, TwDG dg, TwDAT dat, TwMSG msg, ref IntPtr refptr);

            [DllImport("/System/Library/Frameworks/TWAIN.framework/TWAIN", EntryPoint = "DSM_Entry", CharSet = CharSet.Ansi)]
            private static extern TwRC _MacosxDsmRaw([In, Out] TwIdentity origin, IntPtr zeroptr, TwDG dg, TwDAT dat, TwMSG msg, IntPtr rawData);

            [DllImport("/System/Library/Frameworks/TWAIN.framework/TWAIN", EntryPoint = "DSM_Entry", CharSet = CharSet.Ansi)]
            private static extern TwRC _MacosxDsImageXfer([In, Out] TwIdentity origin, [In, Out] TwIdentity dest, TwDG dg, TwDAT dat, TwMSG msg, ref IntPtr hbitmap);

            [DllImport("/System/Library/Frameworks/TWAIN.framework/TWAIN", EntryPoint = "DSM_Entry", CharSet = CharSet.Ansi)]
            private static extern TwRC _MacosxDsRaw([In, Out] TwIdentity origin, [In, Out] TwIdentity dest, TwDG dg, TwDAT dat, TwMSG msg, IntPtr arg);

            #endregion

        }

        /// <summary>
        /// Точки входа для функций управления памятью.
        /// </summary>
        internal sealed class _Memory {
            private static TwEntryPoint _entryPoint;

            /// <summary>
            /// Выделяет блок памяти указанного размера.
            /// </summary>
            /// <param name="size">Размер блока памяти.</param>
            /// <returns>Дескриптор памяти.</returns>
            public static IntPtr Alloc(int size) {
                if(_Memory._entryPoint != null && _Memory._entryPoint.MemoryAllocate != null) {
                    return _Memory._entryPoint.MemoryAllocate(size);
                }
                switch(Environment.OSVersion.Platform) {
                    case PlatformID.Unix:
                    case PlatformID.MacOSX:
                        throw new NotSupportedException();
                    default:
                        return _Memory.GlobalAlloc(0x42, size);
                }
            }

            /// <summary>
            /// Освобождает память.
            /// </summary>
            /// <param name="handle">Дескриптор памяти.</param>
            public static void Free(IntPtr handle) {
                if(_Memory._entryPoint != null && _Memory._entryPoint.MemoryFree != null) {
                    _Memory._entryPoint.MemoryFree(handle);
                    return;
                }
                switch(Environment.OSVersion.Platform) {
                    case PlatformID.Unix:
                    case PlatformID.MacOSX:
                        throw new NotSupportedException();
                    default:
                        _Memory.GlobalFree(handle);
                        break;
                }
            }

            /// <summary>
            /// Выполняет блокировку памяти.
            /// </summary>
            /// <param name="handle">Дескриптор памяти.</param>
            /// <returns>Указатель на блок памяти.</returns>
            public static IntPtr Lock(IntPtr handle) {
                if(_Memory._entryPoint != null && _Memory._entryPoint.MemoryLock != null) {
                    return _Memory._entryPoint.MemoryLock(handle);
                }
                switch(Environment.OSVersion.Platform) {
                    case PlatformID.Unix:
                    case PlatformID.MacOSX:
                        throw new NotSupportedException();
                    default:
                        return _Memory.GlobalLock(handle);
                }
            }

            /// <summary>
            /// Выполняет разблокировку памяти.
            /// </summary>
            /// <param name="handle">Дескриптор памяти.</param>
            public static void Unlock(IntPtr handle) {
                if(_Memory._entryPoint != null && _Memory._entryPoint.MemoryUnlock != null) {
                    _Memory._entryPoint.MemoryUnlock(handle);
                    return;
                }
                switch(Environment.OSVersion.Platform) {
                    case PlatformID.Unix:
                    case PlatformID.MacOSX:
                        throw new NotSupportedException();
                    default:
                        _Memory.GlobalUnlock(handle);
                        break;
                }
            }

            public static void ZeroMemory(IntPtr dest, IntPtr size) {
                switch(Environment.OSVersion.Platform) {
                    case PlatformID.Unix:
                    case PlatformID.MacOSX:
                        byte[] _data = new byte[size.ToInt32()];
                        Marshal.Copy(_data, 0, dest, _data.Length);
                        break;
                    default:
                        _Memory._ZeroMemory(dest, size);
                        break;
                }
            }

            /// <summary>
            /// Устаначливает точки входа.
            /// </summary>
            /// <param name="entry">Точки входа.</param>
            internal static void _SetEntryPoints(TwEntryPoint entry) {
                _Memory._entryPoint = entry;
            }

            #region import kernel32.dll

            [DllImport("kernel32.dll", ExactSpelling = true)]
            private static extern IntPtr GlobalAlloc(int flags, int size);

            [DllImport("kernel32.dll", ExactSpelling = true)]
            private static extern IntPtr GlobalLock(IntPtr handle);

            [DllImport("kernel32.dll", ExactSpelling = true)]
            private static extern bool GlobalUnlock(IntPtr handle);

            [DllImport("kernel32.dll", ExactSpelling = true)]
            private static extern IntPtr GlobalFree(IntPtr handle);

            [DllImport("kernel32.dll", EntryPoint = "RtlZeroMemory", SetLastError = false)]
            private static extern void _ZeroMemory(IntPtr dest, IntPtr size);


            #endregion
        }

        /// <summary>
        /// Точки входа для функций платформы.
        /// </summary>
        internal sealed class _Platform {

            /// <summary>
            /// Загружает указаную библиотеку в память процесса.
            /// </summary>
            /// <param name="fileName">Имя библиотеки.</param>
            /// <returns>Дескриптор модуля.</returns>
            internal static IntPtr Load(string fileName) {
                switch(Environment.OSVersion.Platform) {
                    case PlatformID.Unix:
                    case PlatformID.MacOSX:
                        throw new NotSupportedException();
                    default:
                        return _Platform.LoadLibrary(fileName);
                }
            }

            /// <summary>
            /// Выгружает указаную библиотеку из памяти процесса.
            /// </summary>
            /// <param name="hModule">Дескриптор модуля</param>
            internal static void Unload(IntPtr hModule) {
                switch(Environment.OSVersion.Platform) {
                    case PlatformID.Unix:
                    case PlatformID.MacOSX:
                        break;
                    default:
                        _Platform.FreeLibrary(hModule);
                        break;
                }
            }

            /// <summary>
            /// Возвращает адрес указанной процедуры.
            /// </summary>
            /// <param name="hModule">Дескриптор модуля.</param>
            /// <param name="procName">Имя процедуры.</param>
            /// <returns>Указатель на процедуру.</returns>
            internal static IntPtr GetProcAddr(IntPtr hModule, string procName) {
                switch(Environment.OSVersion.Platform) {
                    case PlatformID.Unix:
                    case PlatformID.MacOSX:
                        throw new NotSupportedException();
                    default:
                        return _Platform.GetProcAddress(hModule, procName);
                }
            }

            [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
            private static extern IntPtr LoadLibrary(string fileName);

            [DllImport("kernel32.dll", ExactSpelling = true)]
            private static extern bool FreeLibrary(IntPtr hModule);

            [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true)]
            private static extern IntPtr GetProcAddress(IntPtr hModule, string procName);
        }

        [Serializable]
        private sealed class _Image {
            private Stream _stream = null;

            private _Image() {
            }

            public static implicit operator _Image(Stream stream) => new _Image { _stream = stream };

            public static implicit operator Stream(_Image image) {
                image._stream.Seek(0L, SeekOrigin.Begin);
                return image._stream;
            }
        }

        /// <summary>
        /// Диапазон значений.
        /// </summary>
        [Serializable]
        public sealed class Range {

            /// <summary>
            /// Prevents a default instance of the <see cref="Range"/> class from being created.
            /// </summary>
            private Range() {
            }

            /// <summary>
            /// Prevents a default instance of the <see cref="Range"/> class from being created.
            /// </summary>
            /// <param name="range">The range.</param>
            private Range(TwRange range) {
                this.MinValue = TwTypeHelper.CastToCommon(range.ItemType, TwTypeHelper.ValueToTw<uint>(range.ItemType, range.MinValue));
                this.MaxValue = TwTypeHelper.CastToCommon(range.ItemType, TwTypeHelper.ValueToTw<uint>(range.ItemType, range.MaxValue));
                this.StepSize = TwTypeHelper.CastToCommon(range.ItemType, TwTypeHelper.ValueToTw<uint>(range.ItemType, range.StepSize));
                this.CurrentValue = TwTypeHelper.CastToCommon(range.ItemType, TwTypeHelper.ValueToTw<uint>(range.ItemType, range.CurrentValue));
                this.DefaultValue = TwTypeHelper.CastToCommon(range.ItemType, TwTypeHelper.ValueToTw<uint>(range.ItemType, range.DefaultValue));
            }

            /// <summary>
            /// Создает и возвращает экземпляр <see cref="Range"/>.
            /// </summary>
            /// <param name="range">Экземпляр <see cref="TwRange"/>.</param>
            /// <returns>Экземпляр <see cref="Range"/>.</returns>
            internal static Range CreateRange(TwRange range) => new Range(range);

            /// <summary>
            /// Создает и возвращает экземпляр <see cref="Range"/>.
            /// </summary>
            /// <param name="minValue">Минимальное значение.</param>
            /// <param name="maxValue">Максимальное значение.</param>
            /// <param name="stepSize">Шаг.</param>
            /// <param name="defaultValue">Значение по умолчанию.</param>
            /// <param name="currentValue">Текущее значение.</param>
            /// <returns>Экземпляр <see cref="Range"/>.</returns>
            public static Range CreateRange(object minValue, object maxValue, object stepSize, object defaultValue, object currentValue) => new Range() {
                MinValue = minValue, MaxValue = maxValue, StepSize = stepSize, DefaultValue = defaultValue, CurrentValue = currentValue
            };

            /// <summary>
            /// Возвращает или устанавливает минимальное значение.
            /// </summary>
            public object MinValue { get; set; }

            /// <summary>
            /// Возвращает или устанавливает максимальное значение.
            /// </summary>
            public object MaxValue { get; set; }

            /// <summary>
            /// Возвращает или устанавливает шаг.
            /// </summary>
            public object StepSize { get; set; }

            /// <summary>
            /// Возвращает или устанавливает значае по умолчанию.
            /// </summary>
            public object DefaultValue { get; set; }

            /// <summary>
            /// Возвращает или устанавливает текущее значение.
            /// </summary>
            public object CurrentValue { get; set; }

            /// <summary>
            /// Конвертирует экземпляр класса в экземпляр <see cref="TwRange"/>.
            /// </summary>
            /// <returns>Экземпляр <see cref="TwRange"/>.</returns>
            internal TwRange ToTwRange() {
                TwType _type = TwTypeHelper.TypeOf(this.CurrentValue.GetType());
                return new TwRange() {
                    ItemType = _type,
                    MinValue = TwTypeHelper.ValueFromTw<uint>(TwTypeHelper.CastToTw(_type, this.MinValue)),
                    MaxValue = TwTypeHelper.ValueFromTw<uint>(TwTypeHelper.CastToTw(_type, this.MaxValue)),
                    StepSize = TwTypeHelper.ValueFromTw<uint>(TwTypeHelper.CastToTw(_type, this.StepSize)),
                    DefaultValue = TwTypeHelper.ValueFromTw<uint>(TwTypeHelper.CastToTw(_type, this.DefaultValue)),
                    CurrentValue = TwTypeHelper.ValueFromTw<uint>(TwTypeHelper.CastToTw(_type, this.CurrentValue))
                };
            }
        }

        /// <summary>
        /// Перечисление.
        /// </summary>
        [Serializable]
        public sealed class Enumeration {
            private object[] _items;

            /// <summary>
            /// Prevents a default instance of the <see cref="Enumeration"/> class from being created.
            /// </summary>
            /// <param name="items">Элементы перечисления.</param>
            /// <param name="currentIndex">Текущий индекс.</param>
            /// <param name="defaultIndex">Индекс по умолчанию.</param>
            private Enumeration(object[] items, int currentIndex, int defaultIndex) {
                this._items = items;
                this.CurrentIndex = currentIndex;
                this.DefaultIndex = defaultIndex;
            }

            /// <summary>
            /// Создает и возвращает экземпляр <see cref="Enumeration"/>.
            /// </summary>
            /// <param name="items">Элементы перечисления.</param>
            /// <param name="currentIndex">Текущий индекс.</param>
            /// <param name="defaultIndex">Индекс по умолчанию.</param>
            /// <returns>Экземпляр <see cref="Enumeration"/>.</returns>
            public static Enumeration CreateEnumeration(object[] items, int currentIndex, int defaultIndex) => new Enumeration(items, currentIndex, defaultIndex);

            /// <summary>
            /// Возвращает количество элементов.
            /// </summary>
            public int Count => this._items.Length;

            /// <summary>
            /// Возвращает текущий индекс.
            /// </summary>
            public int CurrentIndex { get; private set; }

            /// <summary>
            /// Возвращает индекс по умолчанию.
            /// </summary>
            public int DefaultIndex { get; private set; }

            /// <summary>
            /// Возвращает элемент по указанному индексу.
            /// </summary>
            /// <param name="index">Индекс.</param>
            /// <returns>Элемент по указанному индексу.</returns>
            public object this[int index] {
                get {
                    return this._items[index];
                }
                internal set {
                    this._items[index] = value;
                }
            }

            internal object[] Items => this._items;

            /// <summary>
            /// Создает и возвращает экземпляр <see cref="Enumeration"/>.
            /// </summary>
            /// <param name="value">Экземпляр <see cref="Range"/>.</param>
            /// <returns>Экземпляр <see cref="Enumeration"/>.</returns>
            public static Enumeration FromRange(Range value) {
                int _currentIndex = 0, _defaultIndex = 0;
                object[] _items = new object[(int)((Convert.ToSingle(value.MaxValue) - Convert.ToSingle(value.MinValue)) / Convert.ToSingle(value.StepSize)) + 1];
                for(int i = 0; i < _items.Length; i++) {
                    _items[i] = Convert.ToSingle(value.MinValue) + (Convert.ToSingle(value.StepSize) * i);
                    if(Convert.ToSingle(_items[i]) == Convert.ToSingle(value.CurrentValue)) {
                        _currentIndex = i;
                    }
                    if(Convert.ToSingle(_items[i]) == Convert.ToSingle(value.DefaultValue)) {
                        _defaultIndex = i;
                    }
                }
                return Enumeration.CreateEnumeration(_items, _currentIndex, _defaultIndex);
            }

            /// <summary>
            /// Создает и возвращает экземпляр <see cref="Enumeration"/>.
            /// </summary>
            /// <param name="value">Массив значений.</param>
            /// <returns>Экземпляр <see cref="Enumeration"/>.</returns>
            public static Enumeration FromArray(object[] value) => Enumeration.CreateEnumeration(value, 0, 0);

            /// <summary>
            /// Создает и возвращает экземпляр <see cref="Enumeration"/>.
            /// </summary>
            /// <param name="value">Значение.</param>
            /// <returns>Экземпляр <see cref="Enumeration"/>.</returns>
            public static Enumeration FromOneValue(ValueType value) => Enumeration.CreateEnumeration(new object[] { value }, 0, 0);

            internal static Enumeration FromObject(object value) {
                if(value is Range _v1) {
                    return Enumeration.FromRange(_v1);
                }
                if(value is object[] _v2) {
                    return Enumeration.FromArray(_v2);
                }
                if(value is ValueType _v3) {
                    return Enumeration.FromOneValue(_v3);
                }
                if(value is string) {
                    return Enumeration.CreateEnumeration(new object[] { value }, 0, 0);
                }
                return value as Enumeration;
            }
        }

        /// <summary>
        /// Описание изображения.
        /// </summary>
        [Serializable]
        public sealed class ImageInfo {

            private ImageInfo() {
            }

            /// <summary>
            /// Создает и возвращает новый экземпляр класса ImageInfo на основе экземпляра класса TwImageInfo.
            /// </summary>
            /// <param name="info">Описание изображения.</param>
            /// <returns>Экземпляр класса ImageInfo.</returns>
            internal static ImageInfo FromTwImageInfo(TwImageInfo info) => new ImageInfo {
                BitsPerPixel = info.BitsPerPixel,
                BitsPerSample = ImageInfo._Copy(info.BitsPerSample, info.SamplesPerPixel),
                Compression = info.Compression,
                ImageLength = info.ImageLength,
                ImageWidth = info.ImageWidth,
                PixelType = info.PixelType,
                Planar = info.Planar,
                XResolution = info.XResolution,
                YResolution = info.YResolution
            };

            private static short[] _Copy(short[] array, int len) {
                var _result = new short[len];
                for(var i = 0; i < len; i++) {
                    _result[i] = array[i];
                }
                return _result;
            }

            /// <summary>
            /// Resolution in the horizontal
            /// </summary>
            public float XResolution { get; private set; }

            /// <summary>
            /// Resolution in the vertical
            /// </summary>
            public float YResolution { get; private set; }

            /// <summary>
            /// Columns in the image, -1 if unknown by DS
            /// </summary>
            public int ImageWidth { get; private set; }

            /// <summary>
            /// Rows in the image, -1 if unknown by DS
            /// </summary>
            public int ImageLength { get; private set; }

            /// <summary>
            /// Number of bits for each sample
            /// </summary>
            public short[] BitsPerSample { get; private set; }

            /// <summary>
            /// Number of bits for each padded pixel
            /// </summary>
            public short BitsPerPixel { get; private set; }

            /// <summary>
            /// True if Planar, False if chunky
            /// </summary>
            public bool Planar { get; private set; }

            /// <summary>
            /// How to interp data; photo interp
            /// </summary>
            public TwPixelType PixelType { get; private set; }

            /// <summary>
            /// How the data is compressed
            /// </summary>
            public TwCompression Compression { get; private set; }
        }

        /// <summary>
        /// Расширенное описание изображения.
        /// </summary>
        [Serializable]
        public sealed class ExtImageInfo : Collection<ExtImageInfo.InfoItem> {

            private ExtImageInfo() {
            }

            /// <summary>
            /// Создает и возвращает экземпляр класса ExtImageInfo из блока неуправляемой памяти.
            /// </summary>
            /// <param name="ptr">Указатель на блок неуправляемой памяти.</param>
            /// <returns>Экземпляр класса ExtImageInfo.</returns>
            internal static ExtImageInfo FromPtr(IntPtr ptr) {
                int _twExtImageInfoSize = Marshal.SizeOf(typeof(TwExtImageInfo));
                int _twInfoSize = Marshal.SizeOf(typeof(TwInfo));
                TwExtImageInfo _extImageInfo = Marshal.PtrToStructure(ptr, typeof(TwExtImageInfo)) as TwExtImageInfo;
                ExtImageInfo _result = new ExtImageInfo();
                for(int i = 0; i < _extImageInfo.NumInfos; i++) {
                    using(TwInfo _item = Marshal.PtrToStructure((IntPtr)(ptr.ToInt64() + _twExtImageInfoSize + (_twInfoSize * i)), typeof(TwInfo)) as TwInfo) {
                        _result.Add(InfoItem.FromTwInfo(_item));
                    }
                }
                return _result;
            }

            /// <summary>
            /// Возвращает элемент описания расширенной информации о изображении по его коду.
            /// </summary>
            /// <param name="infoId">Код элемента описания расширенной информации о изображении.</param>
            /// <returns>Элемент описания расширенной информации о изображении.</returns>
            /// <exception cref="System.Collections.Generic.KeyNotFoundException">Для указанного кода отсутствует соответствующий элемент.</exception>
            public InfoItem this[TwEI infoId] {
                get {
                    foreach(InfoItem _item in this) {
                        if(_item.InfoId == infoId) {
                            return _item;
                        }
                    }
                    throw new KeyNotFoundException();
                }
            }

            /// <summary>
            /// Элемент описания расширенной информации о изображении.
            /// </summary>
            [Serializable]
            [DebuggerDisplay("InfoId = {InfoId}, IsSuccess = {IsSuccess}, Value = {Value}")]
            public sealed class InfoItem {

                private InfoItem() {
                }

                /// <summary>
                /// Создает и возвращает экземпляр класса элемента описания расширенной информации о изображении из внутреннего экземпляра класса элемента описания расширенной информации о изображении.
                /// </summary>
                /// <param name="info">Внутрений экземпляр класса элемента описания расширенной информации о изображении.</param>
                /// <returns>Экземпляр класса элемента описания расширенной информации о изображении.</returns>
                internal static InfoItem FromTwInfo(TwInfo info) => new InfoItem {
                    InfoId = info.InfoId,
                    IsNotSupported = info.ReturnCode == TwRC.InfoNotSupported,
                    IsNotAvailable = info.ReturnCode == TwRC.DataNotAvailable,
                    IsSuccess = info.ReturnCode == TwRC.Success,
                    Value = info.GetValue()
                };

                /// <summary>
                /// Возвращает код расширенной информации о изображении.
                /// </summary>
                public TwEI InfoId { get; private set; }

                /// <summary>
                /// Возвращает true, если запрошенная информация не поддерживается источником данных; иначе, false.
                /// </summary>
                public bool IsNotSupported { get; private set; }

                /// <summary>
                /// Возвращает true, если запрошенная информация поддерживается источником данных, но в данный момент недоступна; иначе, false.
                /// </summary>
                public bool IsNotAvailable { get; private set; }

                /// <summary>
                /// Возвращает true, если запрошенная информация была успешно извлечена; иначе, false.
                /// </summary>
                public bool IsSuccess { get; private set; }

                /// <summary>
                /// Возвращает значение элемента.
                /// </summary>
                public object Value { get; private set; }
            }
        }

        /// <summary>
        /// Used to pass image data (e.g. in strips) from DS to application.
        /// </summary>
        [Serializable]
        public sealed class ImageMemXfer {

            private ImageMemXfer() {
            }

            internal static ImageMemXfer Create(TwImageMemXfer data) {
                ImageMemXfer _res = new ImageMemXfer() {
                    BytesPerRow = data.BytesPerRow,
                    Columns = data.Columns,
                    Compression = data.Compression,
                    Rows = data.Rows,
                    XOffset = data.XOffset,
                    YOffset = data.YOffset
                };
                if((data.Memory.Flags & TwMF.Handle) != 0) {
                    IntPtr _data = TwainX._Memory.Lock(data.Memory.TheMem);
                    try {
                        _res.ImageData = new byte[data.BytesWritten];
                        Marshal.Copy(_data, _res.ImageData, 0, _res.ImageData.Length);
                    } finally {
                        TwainX._Memory.Unlock(data.Memory.TheMem);
                    }
                } else {
                    _res.ImageData = new byte[data.BytesWritten];
                    Marshal.Copy(data.Memory.TheMem, _res.ImageData, 0, _res.ImageData.Length);
                }
                return _res;
            }

            /// <summary>
            /// How the data is compressed.
            /// </summary>
            public TwCompression Compression { get; private set; }

            /// <summary>
            /// Number of bytes in a row of data.
            /// </summary>
            public uint BytesPerRow { get; private set; }

            /// <summary>
            /// How many columns.
            /// </summary>
            public uint Columns { get; private set; }

            /// <summary>
            /// How many rows.
            /// </summary>
            public uint Rows { get; private set; }

            /// <summary>
            /// How far from the side of the image.
            /// </summary>
            public uint XOffset { get; private set; }

            /// <summary>
            /// How far from the top of the image.
            /// </summary>
            public uint YOffset { get; private set; }

            /// <summary>
            /// Data.
            /// </summary>
            public byte[] ImageData { get; private set; }
        }

        /// <summary>
        /// Описание файла изображения.
        /// </summary>
        [Serializable]
        public sealed class ImageFileXfer {

            /// <summary>
            /// Инициализирует новый экземпляр <see cref="ImageFileXfer"/>.
            /// </summary>
            private ImageFileXfer() {
            }

            /// <summary>
            /// Создает и возвращает новый экземпляр <see cref="ImageFileXfer"/>.
            /// </summary>
            /// <param name="data">Описание файла.</param>
            /// <returns>Экземпляр <see cref="ImageFileXfer"/>.</returns>
            internal static ImageFileXfer Create(TwSetupFileXfer data) => new ImageFileXfer {
                FileName = data.FileName,
                Format = data.Format
            };

            /// <summary>
            /// Возвращает имя файла.
            /// </summary>
            public string FileName { get; private set; }

            /// <summary>
            /// Фозвращает формат файла.
            /// </summary>
            public TwFF Format { get; private set; }
        }

        /// <summary>
        /// Набор операций для работы с цветовой палитрой.
        /// </summary>
        public sealed class TwainPalette : MarshalByRefObject {
            private TwainX _twain;

            /// <summary>
            /// Инициализирует новый экземпляр класса <see cref="TwainPalette"/>.
            /// </summary>
            /// <param name="twain">Экземпляр класса <see cref="TwainPalette"/>.</param>
            internal TwainPalette(TwainX twain) {
                this._twain = twain;
            }

            /// <summary>
            /// Возвращает текущую цветовую палитру.
            /// </summary>
            /// <returns>Экземпляр класса <see cref="TwainPalette"/>.</returns>
            public ColorPalette Get() {
                TwPalette8 _palette = new TwPalette8();
                TwRC _rc = this._twain._dsmEntry.DsInvoke(this._twain._AppId, this._twain._srcds, TwDG.Image, TwDAT.Palette8, TwMSG.Get, ref _palette);
                if(_rc != TwRC.Success) {
                    throw new TwainException(this._twain._GetTwainStatus(), _rc);
                }
                return _palette;
            }

            /// <summary>
            /// Возвращает текущую цветовую палитру, используемую по умолчанию.
            /// </summary>
            /// <returns>Экземпляр класса <see cref="TwainPalette"/>.</returns>
            public ColorPalette GetDefault() {
                TwPalette8 _palette = new TwPalette8();
                TwRC _rc = this._twain._dsmEntry.DsInvoke(this._twain._AppId, this._twain._srcds, TwDG.Image, TwDAT.Palette8, TwMSG.GetDefault, ref _palette);
                if(_rc != TwRC.Success) {
                    throw new TwainException(this._twain._GetTwainStatus(), _rc);
                }
                return _palette;
            }

            /// <summary>
            /// Сбрасывает текущую цветовую палитру и устанавливает указанную.
            /// </summary>
            /// <param name="palette">Экземпляр класса <see cref="TwainPalette"/>.</param>
            public void Reset(ColorPalette palette) {
                TwRC _rc = this._twain._dsmEntry.DsInvoke(this._twain._AppId, this._twain._srcds, TwDG.Image, TwDAT.Palette8, TwMSG.Reset, ref palette);
                if(_rc != TwRC.Success) {
                    throw new TwainException(this._twain._GetTwainStatus(), _rc);
                }
            }

            /// <summary>
            /// Устанавливает указанную цветовую палитру.
            /// </summary>
            /// <param name="palette">Экземпляр класса <see cref="TwainPalette"/>.</param>
            public void Set(ColorPalette palette) {
                TwRC _rc = this._twain._dsmEntry.DsInvoke(this._twain._AppId, this._twain._srcds, TwDG.Image, TwDAT.Palette8, TwMSG.Set, ref palette);
                if(_rc != TwRC.Success) {
                    throw new TwainException(this._twain._GetTwainStatus(), _rc);
                }
            }
        }

        /// <summary>
        /// Цветовая палитра.
        /// </summary>
        [Serializable]
        public sealed class ColorPalette {

            /// <summary>
            /// Инициализирует новый экземпляр <see cref="ColorPalette"/>.
            /// </summary>
            private ColorPalette() {
            }

            /// <summary>
            /// Создает и возвращает новый экземпляр <see cref="ColorPalette"/>.
            /// </summary>
            /// <param name="palette">Цветовая палитра.</param>
            /// <returns>Экземпляр <see cref="ColorPalette"/>.</returns>
            internal static ColorPalette Create(TwPalette8 palette) {
                TwainX.ColorPalette _result = new TwainX.ColorPalette {
                    PaletteType = palette.PaletteType,
                    Colors = new Color[palette.NumColors]
                };
                for(int i = 0; i < palette.NumColors; i++) {
                    _result.Colors[i] = palette.Colors[i];
                }
                return _result;
            }

            /// <summary>
            /// Возвращает тип палитры.
            /// </summary>
            public TwPA PaletteType { get; private set; }

            /// <summary>
            /// Возвращает цвета, входящие в состав палитры.
            /// </summary>
            public Color[] Colors { get; private set; }
        }

        /// <summary>
        /// Identifies the resource.
        /// </summary>
        [Serializable]
        [DebuggerDisplay("{Name}, Version = {Version}")]
        public sealed class Identity {

            /// <summary>
            /// Initializes a new instance of the <see cref="Identity"/> class.
            /// </summary>
            /// <param name="identity">The identity.</param>
            internal Identity(TwIdentity identity) {
                this.Family = identity.ProductFamily;
                this.Manufacturer = identity.Manufacturer;
                this.Name = identity.ProductName;
                this.ProtocolVersion = new Version(identity.ProtocolMajor, identity.ProtocolMinor);
                this.Version = new Version(identity.Version.MajorNum, identity.Version.MinorNum);
            }

            /// <summary>
            /// Get the version of the software.
            /// </summary>
            /// <value>
            /// The version.
            /// </value>
            public Version Version { get; private set; }

            /// <summary>
            /// Get the protocol version.
            /// </summary>
            /// <value>
            /// The protocol version.
            /// </value>
            public Version ProtocolVersion { get; private set; }

            /// <summary>
            /// Get manufacturer name, e.g. "Hewlett-Packard".
            /// </summary>
            public string Manufacturer { get; private set; }

            /// <summary>
            /// Get product family name, e.g. "ScanJet".
            /// </summary>
            public string Family { get; private set; }

            /// <summary>
            /// Get product name, e.g. "ScanJet Plus".
            /// </summary>
            public string Name { get; private set; }
        }

        #endregion

        #region Delegates

        #region DSM delegates DAT_ variants

        private delegate TwRC _DSMparent([In, Out] TwIdentity origin, IntPtr zeroptr, TwDG dg, TwDAT dat, TwMSG msg, ref IntPtr refptr);

        private delegate TwRC _DSMraw([In, Out] TwIdentity origin, IntPtr zeroptr, TwDG dg, TwDAT dat, TwMSG msg, IntPtr rawData);

        #endregion

        #region DS delegates DAT_ variants to DS

        private delegate TwRC _DSixfer([In, Out] TwIdentity origin, [In, Out] TwIdentity dest, TwDG dg, TwDAT dat, TwMSG msg, ref IntPtr hbitmap);

        private delegate TwRC _DSraw([In, Out] TwIdentity origin, [In, Out] TwIdentity dest, TwDG dg, TwDAT dat, TwMSG msg, IntPtr arg);

        #endregion

        internal delegate ImageInfo GetImageInfoCallback();

        internal delegate ExtImageInfo GetExtImageInfoCallback(TwEI[] extInfo);

        private delegate void Action<T>(T arg);

        #endregion
    }
}