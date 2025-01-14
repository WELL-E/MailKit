﻿//
// ImapClient.cs
//
// Author: Jeffrey Stedfast <jeff@xamarin.com>
//
// Copyright (c) 2013-2016 Xamarin Inc. (www.xamarin.com)
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.
//

using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;

#if NETFX_CORE
using Windows.Networking;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;
using Encoding = Portable.Text.Encoding;
#else
using System.Net.Sockets;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
#endif

using MailKit.Security;

namespace MailKit.Net.Imap {
	/// <summary>
	/// An IMAP client that can be used to retrieve messages from a server.
	/// </summary>
	/// <remarks>
	/// The <see cref="ImapClient"/> class supports both the "imap" and "imaps"
	/// protocols. The "imap" protocol makes a clear-text connection to the IMAP
	/// server and does not use SSL or TLS unless the IMAP server supports the
	/// <a href="https://tools.ietf.org/html/rfc3501#section-6.2.1">STARTTLS</a> extension.
	/// The "imaps" protocol, however, connects to the IMAP server using an
	/// SSL-wrapped connection.
	/// </remarks>
	/// <example>
	/// <code language="c#" source="Examples\ImapExamples.cs" region="DownloadMessages"/>
	/// </example>
	public class ImapClient : MailStore
	{
		static readonly char[] ReservedUriCharacters = new [] { ';', '/', '?', ':', '@', '&', '=', '+', '$', ',' };
		const string HexAlphabet = "0123456789ABCDEF";
		readonly ImapEngine engine;
#if NETFX_CORE
		StreamSocket socket;
#endif
		string identifier = null;
		int timeout = 100000;
		bool disposed;
		bool secure;

		/// <summary>
		/// Initializes a new instance of the <see cref="MailKit.Net.Imap.ImapClient"/> class.
		/// </summary>
		/// <remarks>
		/// Before you can retrieve messages with the <see cref="ImapClient"/>, you must first
		/// call one of the <a href="Overload_MailKit_Net_Imap_ImapClient_Connect.htm">Connect</a>
		/// methods and then authenticate with the one of the
		/// <a href="Overload_MailKit_Net_Imap_ImapClient_Authenticate.htm">Authenticate</a>
		/// methods.
		/// </remarks>
		/// <example>
		/// <code language="c#" source="Examples\ImapExamples.cs" region="DownloadMessages"/>
		/// </example>
		public ImapClient () : this (new NullProtocolLogger ())
		{
		}

		/// <summary>
		/// Initializes a new instance of the <see cref="MailKit.Net.Imap.ImapClient"/> class.
		/// </summary>
		/// <remarks>
		/// Before you can retrieve messages with the <see cref="ImapClient"/>, you must first
		/// call one of the <a href="Overload_MailKit_Net_Imap_ImapClient_Connect.htm">Connect</a>
		/// methods and then authenticate with the one of the
		/// <a href="Overload_MailKit_Net_Imap_ImapClient_Authenticate.htm">Authenticate</a>
		/// methods.
		/// </remarks>
		/// <example>
		/// <code language="c#" source="Examples\ImapExamples.cs" region="ProtocolLogger"/>
		/// </example>
		/// <param name="protocolLogger">The protocol logger.</param>
		/// <exception cref="System.ArgumentNullException">
		/// <paramref name="protocolLogger"/> is <c>null</c>.
		/// </exception>
		public ImapClient (IProtocolLogger protocolLogger) : base (protocolLogger)
		{
			// FIXME: should this take a ParserOptions argument?
			engine = new ImapEngine (CreateImapFolder);
			engine.Alert += OnEngineAlert;
		}

		/// <summary>
		/// Gets an object that can be used to synchronize access to the IMAP server.
		/// </summary>
		/// <remarks>
		/// <para>Gets an object that can be used to synchronize access to the IMAP server.</para>
		/// <para>When using the non-Async methods from multiple threads, it is important to lock the
		/// <see cref="SyncRoot"/> object for thread safety when using the synchronous methods.</para>
		/// </remarks>
		/// <value>The lock object.</value>
		public override object SyncRoot {
			get { return engine; }
		}

		/// <summary>
		/// Get the protocol supported by the message service.
		/// </summary>
		/// <remarks>
		/// Gets the protocol supported by the message service.
		/// </remarks>
		/// <value>The protocol.</value>
		protected override string Protocol {
			get { return "imap"; }
		}

		/// <summary>
		/// Get the capabilities supported by the IMAP server.
		/// </summary>
		/// <remarks>
		/// The capabilities will not be known until a successful connection has been made via one of
		/// the <a href="Overload_MailKit_Net_Imap_ImapClient_Connect.htm">Connect</a> methods and may
		/// change as a side-effect of calling one of the
		/// <a href="Overload_MailKit_Net_Imap_ImapClient_Authenticate.htm">Authenticate</a>
		/// methods.
		/// </remarks>
		/// <example>
		/// <code language="c#" source="Examples\ImapExamples.cs" region="Capabilities"/>
		/// </example>
		/// <value>The capabilities.</value>
		/// <exception cref="System.ArgumentException">
		/// Capabilities cannot be enabled, they may only be disabled.
		/// </exception>
		public ImapCapabilities Capabilities {
			get { return engine.Capabilities; }
			set {
				if ((engine.Capabilities | value) > engine.Capabilities)
					throw new ArgumentException ("Capabilities cannot be enabled, they may only be disabled.", "value");

				engine.Capabilities = value;
			}
		}

		/// <summary>
		/// Gets the internationalization level supported by the IMAP server.
		/// </summary>
		/// <remarks>
		/// <para>Gets the internationalization level supported by the IMAP server.</para>
		/// <para>For more information, see
		/// <a href="https://tools.ietf.org/html/rfc5255#section-4">section 4 of rfc5255</a>.</para>
		/// </remarks>
		/// <value>The internationalization level.</value>
		public int InternationalizationLevel {
			get { return engine.I18NLevel; }
		}

		/// <summary>
		/// Get the access rights supported by the IMAP server.
		/// </summary>
		/// <remarks>
		/// These rights are additional rights supported by the IMAP server beyond the standard rights
		/// defined in <a href="https://tools.ietf.org/html/rfc4314#section-2.1">section 2.1 of rfc4314</a>
		/// and will not be populated until the client is successfully connected.
		/// </remarks>
		/// <example>
		/// <code language="c#" source="Examples\ImapExamples.cs" region="Capabilities"/>
		/// </example>
		/// <value>The rights.</value>
		public AccessRights Rights {
			get { return engine.Rights; }
		}

		void CheckDisposed ()
		{
			if (disposed)
				throw new ObjectDisposedException ("ImapClient");
		}

		void CheckConnected ()
		{
			if (!IsConnected)
				throw new ServiceNotConnectedException ("The ImapClient is not connected.");
		}

		void CheckAuthenticated ()
		{
			if (!IsAuthenticated)
				throw new ServiceNotAuthenticatedException ("The ImapClient is not authenticated.");
		}

		/// <summary>
		/// Instantiate a new <see cref="ImapFolder"/>.
		/// </summary>
		/// <remarks>
		/// <para>Creates a new <see cref="ImapFolder"/> instance.</para>
		/// <note type="note">This method's purpose is to allow subclassing <see cref="ImapFolder"/>.</note>
		/// </remarks>
		/// <returns>The IMAP folder instance.</returns>
		/// <param name="args">The constructior arguments.</param>
		/// <exception cref="System.ArgumentNullException">
		/// <paramref name="args"/> is <c>null</c>.
		/// </exception>
		protected virtual ImapFolder CreateImapFolder (ImapFolderConstructorArgs args)
		{
			return new ImapFolder (args);
		}

#if !NETFX_CORE
		bool ValidateRemoteCertificate (object sender, X509Certificate certificate, X509Chain chain, SslPolicyErrors sslPolicyErrors)
		{
			if (ServerCertificateValidationCallback != null)
				return ServerCertificateValidationCallback (engine.Uri.Host, certificate, chain, sslPolicyErrors);

#if !COREFX
			if (ServicePointManager.ServerCertificateValidationCallback != null)
				return ServicePointManager.ServerCertificateValidationCallback (engine.Uri.Host, certificate, chain, sslPolicyErrors);
#endif

			return sslPolicyErrors == SslPolicyErrors.None;
		}
#endif

		/// <summary>
		/// Enable compression over the IMAP connection.
		/// </summary>
		/// <remarks>
		/// <para>Enables compression over the IMAP connection.</para>
		/// <para>If the IMAP server supports the <see cref="ImapCapabilities.Compress"/> extension,
		/// it is possible at any point after connecting to enable compression to reduce network
		/// bandwidth usage. Ideally, this method should be called before authenticating.</para>
		/// </remarks>
		/// <param name="cancellationToken">The cancellation token.</param>
		/// <exception cref="System.ObjectDisposedException">
		/// The <see cref="ImapClient"/> has been disposed.
		/// </exception>
		/// <exception cref="ServiceNotConnectedException">
		/// The <see cref="ImapClient"/> is not connected.
		/// </exception>
		/// <exception cref="System.InvalidOperationException">
		/// Compression must be enabled before a folder has been selected.
		/// </exception>
		/// <exception cref="System.OperationCanceledException">
		/// The operation was canceled via the cancellation token.
		/// </exception>
		/// <exception cref="System.IO.IOException">
		/// An I/O error occurred.
		/// </exception>
		/// <exception cref="ImapCommandException">
		/// The server replied to the COMPRESS command with a NO or BAD response.
		/// </exception>
		/// <exception cref="ImapProtocolException">
		/// An IMAP protocol error occurred.
		/// </exception>
		public void Compress (CancellationToken cancellationToken = default (CancellationToken))
		{
			CheckDisposed ();
			CheckConnected ();

			if ((engine.Capabilities & ImapCapabilities.Compress) == 0)
				throw new NotSupportedException ("The IMAP server does not support the COMPRESS extension.");

			if (engine.State >= ImapEngineState.Selected)
				throw new InvalidOperationException ("Compression must be enabled before selecting a folder.");

			int capabilitiesVersion = engine.CapabilitiesVersion;
			var ic = engine.QueueCommand (cancellationToken, null, "COMPRESS DEFLATE\r\n");

			engine.Wait (ic);

			if (ic.Response != ImapCommandResponse.Ok) {
				for (int i = 0; i < ic.RespCodes.Count; i++) {
					if (ic.RespCodes[i].Type == ImapResponseCodeType.CompressionActive)
						return;
				}

				throw ImapCommandException.Create ("COMPRESS", ic);
			}

			engine.Stream.Stream = new CompressedStream (engine.Stream.Stream);

			// Query the CAPABILITIES again if the server did not include an
			// untagged CAPABILITIES response to the COMPRESS command.
			if (engine.CapabilitiesVersion == capabilitiesVersion)
				engine.QueryCapabilities (cancellationToken);
		}

		/// <summary>
		/// Asynchronously enable compression over the IMAP connection.
		/// </summary>
		/// <remarks>
		/// <para>Asynchronously enables compression over the IMAP connection.</para>
		/// <para>If the IMAP server supports the <see cref="ImapCapabilities.Compress"/> extension,
		/// it is possible at any point after connecting to enable compression to reduce network
		/// bandwidth usage. Ideally, this method should be called before authenticating.</para>
		/// </remarks>
		/// <returns>An asynchronous task context.</returns>
		/// <param name="cancellationToken">The cancellation token.</param>
		/// <exception cref="System.ObjectDisposedException">
		/// The <see cref="ImapClient"/> has been disposed.
		/// </exception>
		/// <exception cref="ServiceNotConnectedException">
		/// The <see cref="ImapClient"/> is not connected.
		/// </exception>
		/// <exception cref="System.InvalidOperationException">
		/// Compression must be enabled before a folder has been selected.
		/// </exception>
		/// <exception cref="System.NotSupportedException">
		/// The IMAP server does not support the COMPRESS extension.
		/// </exception>
		/// <exception cref="System.OperationCanceledException">
		/// The operation was canceled via the cancellation token.
		/// </exception>
		/// <exception cref="System.IO.IOException">
		/// An I/O error occurred.
		/// </exception>
		/// <exception cref="ImapCommandException">
		/// The server replied to the COMPRESS command with a NO or BAD response.
		/// </exception>
		/// <exception cref="ImapProtocolException">
		/// An IMAP protocol error occurred.
		/// </exception>
		public Task CompressAsync (CancellationToken cancellationToken = default (CancellationToken))
		{
			return Task.Factory.StartNew (() => {
				lock (SyncRoot) {
					Compress (cancellationToken);
				}
			}, cancellationToken, TaskCreationOptions.None, TaskScheduler.Default);
		}

		/// <summary>
		/// Enable the QRESYNC feature.
		/// </summary>
		/// <remarks>
		/// <para>Enables the QRESYNC feature.</para>
		/// <para>The QRESYNC extension improves resynchronization performance of folders by
		/// querying the IMAP server for a list of changes when the folder is opened using the
		/// <see cref="ImapFolder.Open(FolderAccess,uint,ulong,System.Collections.Generic.IList&lt;UniqueId&gt;,System.Threading.CancellationToken)"/>
		/// method.</para>
		/// <para>If this feature is enabled, the <see cref="MailFolder.MessageExpunged"/> event is replaced
		/// with the <see cref="MailFolder.MessagesVanished"/> event.</para>
		/// <para>This method needs to be called immediately after calling one of the
		/// <a href="Overload_MailKit_Net_Imap_ImapClient_Authenticate.htm">Authenticate</a> methods, before
		/// opening any folders.</para>
		/// </remarks>
		/// <param name="cancellationToken">The cancellation token.</param>
		/// <exception cref="System.ObjectDisposedException">
		/// The <see cref="ImapClient"/> has been disposed.
		/// </exception>
		/// <exception cref="ServiceNotConnectedException">
		/// The <see cref="ImapClient"/> is not connected.
		/// </exception>
		/// <exception cref="ServiceNotAuthenticatedException">
		/// The <see cref="ImapClient"/> is not authenticated.
		/// </exception>
		/// <exception cref="System.InvalidOperationException">
		/// Quick resynchronization needs to be enabled before selecting a folder.
		/// </exception>
		/// <exception cref="System.NotSupportedException">
		/// The IMAP server does not support the QRESYNC extension.
		/// </exception>
		/// <exception cref="System.OperationCanceledException">
		/// The operation was canceled via the cancellation token.
		/// </exception>
		/// <exception cref="System.IO.IOException">
		/// An I/O error occurred.
		/// </exception>
		/// <exception cref="ImapCommandException">
		/// The server replied to the ENABLE command with a NO or BAD response.
		/// </exception>
		/// <exception cref="ImapProtocolException">
		/// An IMAP protocol error occurred.
		/// </exception>
		public override void EnableQuickResync (CancellationToken cancellationToken = default (CancellationToken))
		{
			CheckDisposed ();
			CheckConnected ();
			CheckAuthenticated ();

			if (engine.State != ImapEngineState.Authenticated)
				throw new InvalidOperationException ("QRESYNC needs to be enabled immediately after authenticating.");

			if ((engine.Capabilities & ImapCapabilities.QuickResync) == 0)
				throw new NotSupportedException ("The IMAP server does not support the QRESYNC extension.");

			if (engine.QResyncEnabled)
				return;

			var ic = engine.QueueCommand (cancellationToken, null, "ENABLE QRESYNC CONDSTORE\r\n");

			engine.Wait (ic);

			if (ic.Response != ImapCommandResponse.Ok)
				throw ImapCommandException.Create ("ENABLE", ic);

			engine.QResyncEnabled = true;
		}

		/// <summary>
		/// Enable the UTF8=ACCEPT extension.
		/// </summary>
		/// <remarks>
		/// Enables the UTF8=ACCEPT extension.
		/// </remarks>
		/// <param name="cancellationToken">The cancellation token.</param>
		/// <exception cref="System.ObjectDisposedException">
		/// The <see cref="ImapClient"/> has been disposed.
		/// </exception>
		/// <exception cref="ServiceNotConnectedException">
		/// The <see cref="ImapClient"/> is not connected.
		/// </exception>
		/// <exception cref="ServiceNotAuthenticatedException">
		/// The <see cref="ImapClient"/> is not authenticated.
		/// </exception>
		/// <exception cref="System.InvalidOperationException">
		/// UTF8=ACCEPT needs to be enabled before selecting a folder.
		/// </exception>
		/// <exception cref="System.NotSupportedException">
		/// The IMAP server does not support the UTF8=ACCEPT extension.
		/// </exception>
		/// <exception cref="System.OperationCanceledException">
		/// The operation was canceled via the cancellation token.
		/// </exception>
		/// <exception cref="System.IO.IOException">
		/// An I/O error occurred.
		/// </exception>
		/// <exception cref="ImapCommandException">
		/// The server replied to the ENABLE command with a NO or BAD response.
		/// </exception>
		/// <exception cref="ImapProtocolException">
		/// An IMAP protocol error occurred.
		/// </exception>
		public void EnableUTF8 (CancellationToken cancellationToken = default (CancellationToken))
		{
			CheckDisposed ();
			CheckConnected ();
			CheckAuthenticated ();

			if (engine.State != ImapEngineState.Authenticated)
				throw new InvalidOperationException ("UTF8=ACCEPT needs to be enabled immediately after authenticating.");

			if ((engine.Capabilities & ImapCapabilities.UTF8Accept) == 0)
				throw new NotSupportedException ("The IMAP server does not support the UTF8=ACCEPT extension.");

			if (engine.UTF8Enabled)
				return;

			var ic = engine.QueueCommand (cancellationToken, null, "ENABLE UTF8=ACCEPT\r\n");

			engine.Wait (ic);

			if (ic.Response != ImapCommandResponse.Ok)
				throw ImapCommandException.Create ("ENABLE", ic);

			engine.UTF8Enabled = true;
		}

		/// <summary>
		/// Enable the UTF8=ACCEPT extension.
		/// </summary>
		/// <remarks>
		/// Enables the UTF8=ACCEPT extension.
		/// </remarks>
		/// <returns>An asynchronous task context.</returns>
		/// <param name="cancellationToken">The cancellation token.</param>
		/// <exception cref="System.ObjectDisposedException">
		/// The <see cref="ImapClient"/> has been disposed.
		/// </exception>
		/// <exception cref="ServiceNotConnectedException">
		/// The <see cref="ImapClient"/> is not connected.
		/// </exception>
		/// <exception cref="ServiceNotAuthenticatedException">
		/// The <see cref="ImapClient"/> is not authenticated.
		/// </exception>
		/// <exception cref="System.InvalidOperationException">
		/// UTF8=ACCEPT needs to be enabled before selecting a folder.
		/// </exception>
		/// <exception cref="System.NotSupportedException">
		/// The IMAP server does not support the UTF8=ACCEPT extension.
		/// </exception>
		/// <exception cref="System.OperationCanceledException">
		/// The operation was canceled via the cancellation token.
		/// </exception>
		/// <exception cref="System.IO.IOException">
		/// An I/O error occurred.
		/// </exception>
		/// <exception cref="ImapCommandException">
		/// The server replied to the ENABLE command with a NO or BAD response.
		/// </exception>
		/// <exception cref="ImapProtocolException">
		/// An IMAP protocol error occurred.
		/// </exception>
		public Task EnableUTF8Async (CancellationToken cancellationToken = default (CancellationToken))
		{
			return Task.Factory.StartNew (() => {
				lock (SyncRoot) {
					EnableUTF8 (cancellationToken);
				}
			}, cancellationToken, TaskCreationOptions.None, TaskScheduler.Default);
		}

		/// <summary>
		/// Identify the client implementation to the server and obtain the server implementation details.
		/// </summary>
		/// <remarks>
		/// <para>Passes along the client implementation details to the server while also obtaining implementation
		/// details from the server.</para>
		/// <para>If the <paramref name="clientImplementation"/> is <c>null</c> or no properties have been set, no
		/// identifying information will be sent to the server.</para>
		/// <note type="security">
		/// <para>Security Implications</para>
		/// <para>This command has the danger of violating the privacy of users if misused. Clients should
		/// notify users that they send the ID command.</para>
		/// <para>It is highly desirable that implementations provide a method of disabling ID support, perhaps by
		/// not calling this method at all, or by passing <c>null</c> as the <paramref name="clientImplementation"/>
		/// argument.</para>
		/// <para>Implementors must exercise extreme care in adding properties to the <paramref name="clientImplementation"/>.
		/// Some properties, such as a processor ID number, Ethernet address, or other unique (or mostly unique) identifier
		/// would allow tracking of users in ways that violate user privacy expectations and may also make it easier for
		/// attackers to exploit security holes in the client.</para>
		/// </note>
		/// </remarks>
		/// <example>
		/// <code language="c#" source="Examples\ImapExamples.cs" region="Capabilities"/>
		/// </example>
		/// <returns>The implementation details of the server if available; otherwise, <c>null</c>.</returns>
		/// <param name="clientImplementation">The client implementation.</param>
		/// <param name="cancellationToken">The cancellation token.</param>
		/// <exception cref="System.ObjectDisposedException">
		/// The <see cref="ImapClient"/> has been disposed.
		/// </exception>
		/// <exception cref="ServiceNotConnectedException">
		/// The <see cref="ImapClient"/> is not connected.
		/// </exception>
		/// <exception cref="System.NotSupportedException">
		/// The IMAP server does not support the ID extension.
		/// </exception>
		/// <exception cref="System.OperationCanceledException">
		/// The operation was canceled via the cancellation token.
		/// </exception>
		/// <exception cref="System.IO.IOException">
		/// An I/O error occurred.
		/// </exception>
		/// <exception cref="ImapCommandException">
		/// The server replied to the ID command with a NO or BAD response.
		/// </exception>
		/// <exception cref="ImapProtocolException">
		/// An IMAP protocol error occurred.
		/// </exception>
		public ImapImplementation Identify (ImapImplementation clientImplementation, CancellationToken cancellationToken = default (CancellationToken))
		{
			CheckDisposed ();
			CheckConnected ();

			if ((engine.Capabilities & ImapCapabilities.Id) == 0)
				throw new NotSupportedException ("The IMAP server does not support the ID extension.");

			var command = new StringBuilder ("ID ");
			var args = new List<object> ();

			if (clientImplementation != null && clientImplementation.Properties.Count > 0) {
				command.Append ('(');
				foreach (var property in clientImplementation.Properties) {
					command.Append ("%Q ");
					args.Add (property.Key);

					if (property.Value != null) {
						command.Append ("%Q ");
						args.Add (property.Value);
					} else {
						command.Append ("NIL ");
					}
				}
				command[command.Length - 1] = ')';
				command.Append ("\r\n");
			} else {
				command.Append ("NIL\r\n");
			}

			var ic = new ImapCommand (engine, cancellationToken, null, command.ToString (), args.ToArray ());
			ic.RegisterUntaggedHandler ("ID", ImapUtils.ParseImplementation);

			engine.QueueCommand (ic);
			engine.Wait (ic);

			if (ic.Response != ImapCommandResponse.Ok)
				throw ImapCommandException.Create ("ID", ic);

			return (ImapImplementation) ic.UserData;
		}

		/// <summary>
		/// Asynchronously identify the client implementation to the server and obtain the server implementation details.
		/// </summary>
		/// <remarks>
		/// <para>Passes along the client implementation details to the server while also obtaining implementation
		/// details from the server.</para>
		/// <para>If the <paramref name="clientImplementation"/> is <c>null</c> or no properties have been set, no
		/// identifying information will be sent to the server.</para>
		/// <note type="security">
		/// <para>Security Implications</para>
		/// <para>This command has the danger of violating the privacy of users if misused. Clients should
		/// notify users that they send the ID command.</para>
		/// <para>It is highly desirable that implementations provide a method of disabling ID support, perhaps by
		/// not calling this method at all, or by passing <c>null</c> as the <paramref name="clientImplementation"/>
		/// argument.</para>
		/// <para>Implementors must exercise extreme care in adding properties to the <paramref name="clientImplementation"/>.
		/// Some properties, such as a processor ID number, Ethernet address, or other unique (or mostly unique) identifier
		/// would allow tracking of users in ways that violate user privacy expectations and may also make it easier for
		/// attackers to exploit security holes in the client.</para>
		/// </note>
		/// </remarks>
		/// <returns>The implementation details of the server if available; otherwise, <c>null</c>.</returns>
		/// <param name="clientImplementation">The client implementation.</param>
		/// <param name="cancellationToken">The cancellation token.</param>
		/// <exception cref="System.ObjectDisposedException">
		/// The <see cref="ImapClient"/> has been disposed.
		/// </exception>
		/// <exception cref="ServiceNotConnectedException">
		/// The <see cref="ImapClient"/> is not connected.
		/// </exception>
		/// <exception cref="System.NotSupportedException">
		/// The IMAP server does not support the ID extension.
		/// </exception>
		/// <exception cref="System.OperationCanceledException">
		/// The operation was canceled via the cancellation token.
		/// </exception>
		/// <exception cref="System.IO.IOException">
		/// An I/O error occurred.
		/// </exception>
		/// <exception cref="ImapCommandException">
		/// The server replied to the ID command with a NO or BAD response.
		/// </exception>
		/// <exception cref="ImapProtocolException">
		/// An IMAP protocol error occurred.
		/// </exception>
		public Task<ImapImplementation> IdentifyAsync (ImapImplementation clientImplementation, CancellationToken cancellationToken = default (CancellationToken))
		{
			return Task.Factory.StartNew (() => {
				lock (SyncRoot) {
					return Identify (clientImplementation, cancellationToken);
				}
			}, cancellationToken, TaskCreationOptions.None, TaskScheduler.Default);
		}

		#region IMailService implementation

		/// <summary>
		/// Get the authentication mechanisms supported by the IMAP server.
		/// </summary>
		/// <remarks>
		/// <para>The authentication mechanisms are queried as part of the
		/// <a href="Overload_MailKit_Net_Imap_ImapClient_Connect.htm">Connect</a>
		/// method.</para>
		/// <note type="tip">To prevent the usage of certain authentication mechanisms,
		/// simply remove them from the <see cref="AuthenticationMechanisms"/> hash set
		/// before authenticating.</note>
		/// </remarks>
		/// <example>
		/// <code language="c#" source="Examples\ImapExamples.cs" region="Capabilities"/>
		/// </example>
		/// <value>The authentication mechanisms.</value>
		public override HashSet<string> AuthenticationMechanisms {
			get { return engine.AuthenticationMechanisms; }
		}

		/// <summary>
		/// Get the threading algorithms supported by the IMAP server.
		/// </summary>
		/// <remarks>
		/// The threading algorithms are queried as part of the
		/// <a href="Overload_MailKit_Net_Imap_ImapClient_Connect.htm">Connect</a>
		/// and <a href="Overload_MailKit_Net_Imap_ImapClient_Authenticate.htm">Authenticate</a> methods.
		/// </remarks>
		/// <example>
		/// <code language="c#" source="Examples\ImapExamples.cs" region="Capabilities"/>
		/// </example>
		/// <value>The authentication mechanisms.</value>
		public HashSet<ThreadingAlgorithm> ThreadingAlgorithms {
			get { return engine.ThreadingAlgorithms; }
		}

		/// <summary>
		/// Get or set the timeout for network streaming operations, in milliseconds.
		/// </summary>
		/// <remarks>
		/// Gets or sets the underlying socket stream's <see cref="System.IO.Stream.ReadTimeout"/>
		/// and <see cref="System.IO.Stream.WriteTimeout"/> values.
		/// </remarks>
		/// <value>The timeout in milliseconds.</value>
		public override int Timeout {
			get { return timeout; }
			set {
				if (IsConnected && engine.Stream.CanTimeout) {
					engine.Stream.WriteTimeout = value;
					engine.Stream.ReadTimeout = value;
				}

				timeout = value;
			}
		}

		/// <summary>
		/// Get whether or not the client is currently connected to an IMAP server.
		/// </summary>
		/// <remarks>
		/// When an <see cref="ImapProtocolException"/> is caught, the connection state of the
		/// <see cref="ImapClient"/> should be checked before continuing.
		/// </remarks>
		/// <value><c>true</c> if the client is connected; otherwise, <c>false</c>.</value>
		public override bool IsConnected {
			get { return engine.IsConnected; }
		}

		/// <summary>
		/// Get whether or not the connection is secure (typically via SSL or TLS).
		/// </summary>
		/// <remarks>
		/// Gets whether or not the connection is secure (typically via SSL or TLS).
		/// </remarks>
		/// <value><c>true</c> if the connection is secure; otherwise, <c>false</c>.</value>
		public override bool IsSecure {
			get { return IsConnected && secure; }
		}

		/// <summary>
		/// Get whether or not the client is currently authenticated with the IMAP server.
		/// </summary>
		/// <remarks>
		/// <para>Gets whether or not the client is currently authenticated with the IMAP server.</para>
		/// <para>To authenticate with the IMAP server, use one of the
		/// <a href="Overload_MailKit_Net_Imap_ImapClient_Authenticate.htm">Authenticate</a>
		/// methods.</para>
		/// </remarks>
		/// <value><c>true</c> if the client is connected; otherwise, <c>false</c>.</value>
		public override bool IsAuthenticated {
			get { return engine.State >= ImapEngineState.Authenticated; }
		}

		/// <summary>
		/// Get whether or not the client is currently in the IDLE state.
		/// </summary>
		/// <remarks>
		/// Gets whether or not the client is currently in the IDLE state.
		/// </remarks>
		/// <value><c>true</c> if an IDLE command is active; otherwise, <c>false</c>.</value>
		public bool IsIdle {
			get { return engine.State == ImapEngineState.Idle; }
		}

		static AuthenticationException CreateAuthenticationException (ImapCommand ic)
		{
			if (string.IsNullOrEmpty (ic.ResponseText)) {
				for (int i = 0; i < ic.RespCodes.Count; i++) {
					if (ic.RespCodes[i].IsError)
						return new AuthenticationException (ic.RespCodes[i].Message);
				}

				return new AuthenticationException ();
			}

			return new AuthenticationException (ic.ResponseText);
		}

		static bool IsHexDigit (char c)
		{
			return (c >= '0' && c <= '9') || (c >= 'A' && c <= 'F') || (c >= 'a' && c <= 'f');
		}

		static char HexUnescape (string pattern, ref int index)
		{
			uint value, c;

			if (pattern[index++] != '%' || !IsHexDigit (pattern[index]) || !IsHexDigit (pattern[index + 1]))
				return '%';

			c = (uint) pattern[index++];

			if (c >= 'a')
				value = (((c - 'a') + 10) << 4);
			else if (c >= 'A')
				value = (((c - 'A') + 10) << 4);
			else
				value = ((c - '0') << 4);

			c = pattern[index++];

			if (c >= 'a')
				value |= ((c - 'a') + 10);
			else if (c >= 'A')
				value |= ((c - 'A') + 10);
			else
				value |= (c - '0');

			return (char) value;
		}

		static string UnescapeUserName (string escaped)
		{
			StringBuilder userName;
			int startIndex, index;

			if ((index = escaped.IndexOf ('%')) == -1)
				return escaped;

			userName = new StringBuilder ();
			startIndex = 0;

			do {
				userName.Append (escaped, startIndex, index - startIndex);
				userName.Append (HexUnescape (escaped, ref index));
				startIndex = index;

				if (startIndex >= escaped.Length)
					break;

				index = escaped.IndexOf ('%', startIndex);
			} while (index != -1);

			if (index == -1)
				userName.Append (escaped, startIndex, escaped.Length - startIndex);

			return userName.ToString ();
		}

		static string HexEscape (char c)
		{
			return "%" + HexAlphabet[(c >> 4) & 0xF] + HexAlphabet[c & 0xF];
		}

		static string EscapeUserName (string userName)
		{
			StringBuilder escaped;
			int startIndex, index;

			if ((index = userName.IndexOfAny (ReservedUriCharacters)) == -1)
				return userName;

			escaped = new StringBuilder ();
			startIndex = 0;

			do {
				escaped.Append (userName, startIndex, index - startIndex);
				escaped.Append (HexEscape (userName[index++]));
				startIndex = index;

				if (startIndex >= userName.Length)
					break;

				index = userName.IndexOfAny (ReservedUriCharacters, startIndex);
			} while (index != -1);

			if (index == -1)
				escaped.Append (userName, startIndex, userName.Length - startIndex);

			return escaped.ToString ();
		}

		string GetSessionIdentifier (string userName)
		{
			var uri = engine.Uri;

			return string.Format ("{0}://{1}@{2}:{3}", uri.Scheme, EscapeUserName (userName), uri.Host, uri.Port);
		}

		/// <summary>
		/// Authenticate using the supplied credentials.
		/// </summary>
		/// <remarks>
		/// <para>If the IMAP server supports one or more SASL authentication mechanisms,
		/// then the SASL mechanisms that both the client and server support are tried
		/// in order of greatest security to weakest security. Once a SASL
		/// authentication mechanism is found that both client and server support,
		/// the credentials are used to authenticate.</para>
		/// <para>If the server does not support SASL or if no common SASL mechanisms
		/// can be found, then LOGIN command is used as a fallback.</para>
		/// <note type="tip">To prevent the usage of certain authentication mechanisms,
		/// simply remove them from the <see cref="AuthenticationMechanisms"/> hash set
		/// before calling this method.</note>
		/// </remarks>
		/// <param name="encoding">The text encoding to use for the user's credentials.</param>
		/// <param name="credentials">The user's credentials.</param>
		/// <param name="cancellationToken">The cancellation token.</param>
		/// <exception cref="System.ArgumentNullException">
		/// <para><paramref name="encoding"/> is <c>null</c>.</para>
		/// <para>-or-</para>
		/// <para><paramref name="credentials"/> is <c>null</c>.</para>
		/// </exception>
		/// <exception cref="System.ObjectDisposedException">
		/// The <see cref="ImapClient"/> has been disposed.
		/// </exception>
		/// <exception cref="ServiceNotConnectedException">
		/// The <see cref="ImapClient"/> is not connected.
		/// </exception>
		/// <exception cref="System.InvalidOperationException">
		/// The <see cref="ImapClient"/> is already authenticated.
		/// </exception>
		/// <exception cref="System.OperationCanceledException">
		/// The operation was canceled via the cancellation token.
		/// </exception>
		/// <exception cref="MailKit.Security.AuthenticationException">
		/// Authentication using the supplied credentials has failed.
		/// </exception>
		/// <exception cref="MailKit.Security.SaslException">
		/// A SASL authentication error occurred.
		/// </exception>
		/// <exception cref="System.IO.IOException">
		/// An I/O error occurred.
		/// </exception>
		/// <exception cref="ImapProtocolException">
		/// An IMAP protocol error occurred.
		/// </exception>
		public override void Authenticate (Encoding encoding, ICredentials credentials, CancellationToken cancellationToken = default (CancellationToken))
		{
			if (encoding == null)
				throw new ArgumentNullException ("encoding");

			if (credentials == null)
				throw new ArgumentNullException ("credentials");

			CheckDisposed ();
			CheckConnected ();

			if (engine.State >= ImapEngineState.Authenticated)
				throw new InvalidOperationException ("The ImapClient is already authenticated.");

			int capabilitiesVersion = engine.CapabilitiesVersion;
			var uri = new Uri ("imap://" + engine.Uri.Host);
			NetworkCredential cred;
			ImapCommand ic = null;
			SaslMechanism sasl;
			string id;

			foreach (var authmech in SaslMechanism.AuthMechanismRank) {
				if (!engine.AuthenticationMechanisms.Contains (authmech))
					continue;

				if ((sasl = SaslMechanism.Create (authmech, uri, encoding, credentials)) == null)
					continue;

				cancellationToken.ThrowIfCancellationRequested ();

				var command = string.Format ("AUTHENTICATE {0}", sasl.MechanismName);

				if ((engine.Capabilities & ImapCapabilities.SaslIR) != 0 && sasl.SupportsInitialResponse) {
					var ir = sasl.Challenge (null);
					command += " " + ir + "\r\n";
				} else {
					command += "\r\n";
				}

				ic = engine.QueueCommand (cancellationToken, null, command);
				ic.ContinuationHandler = (imap, cmd, text) => {
					string challenge;

					if (sasl.IsAuthenticated) {
						// The server claims we aren't done authenticating, but our SASL mechanism thinks we are...
						// Send an empty string to abort the AUTHENTICATE command.
						challenge = string.Empty;
					} else {
						challenge = sasl.Challenge (text);
					}

					var buf = Encoding.ASCII.GetBytes (challenge + "\r\n");
					imap.Stream.Write (buf, 0, buf.Length, cmd.CancellationToken);
					imap.Stream.Flush (cmd.CancellationToken);
				};

				engine.Wait (ic);

				if (ic.Response != ImapCommandResponse.Ok)
					continue;

				engine.State = ImapEngineState.Authenticated;

				cred = credentials.GetCredential (uri, sasl.MechanismName);
				id = GetSessionIdentifier (cred.UserName);
				if (id != identifier) {
					engine.FolderCache.Clear ();
					identifier = id;
				}

				// Query the CAPABILITIES again if the server did not include an
				// untagged CAPABILITIES response to the AUTHENTICATE command.
				if (engine.CapabilitiesVersion == capabilitiesVersion)
					engine.QueryCapabilities (cancellationToken);

				engine.QueryNamespaces (cancellationToken);
				engine.QuerySpecialFolders (cancellationToken);
				OnAuthenticated (ic.ResponseText);
				return;
			}

			if ((Capabilities & ImapCapabilities.LoginDisabled) != 0) {
				if (ic == null)
					throw new AuthenticationException ("The LOGIN command is disabled.");

				throw CreateAuthenticationException (ic);
			}

			// fall back to the classic LOGIN command...
			cred = credentials.GetCredential (uri, "DEFAULT");

			ic = engine.QueueCommand (cancellationToken, null, "LOGIN %S %S\r\n", cred.UserName, cred.Password);

			engine.Wait (ic);

			if (ic.Response != ImapCommandResponse.Ok)
				throw CreateAuthenticationException (ic);

			engine.State = ImapEngineState.Authenticated;

			id = GetSessionIdentifier (cred.UserName);
			if (id != identifier) {
				engine.FolderCache.Clear ();
				identifier = id;
			}

			// Query the CAPABILITIES again if the server did not include an
			// untagged CAPABILITIES response to the LOGIN command.
			if (engine.CapabilitiesVersion == capabilitiesVersion)
				engine.QueryCapabilities (cancellationToken);

			engine.QueryNamespaces (cancellationToken);
			engine.QuerySpecialFolders (cancellationToken);
			OnAuthenticated (ic.ResponseText);
		}

		internal void ReplayConnect (string host, Stream replayStream, CancellationToken cancellationToken = default (CancellationToken))
		{
			CheckDisposed ();

			if (host == null)
				throw new ArgumentNullException ("host");

			if (replayStream == null)
				throw new ArgumentNullException ("replayStream");

			engine.Uri = new Uri ("imap://" + host);
			engine.Connect (new ImapStream (replayStream, null, ProtocolLogger), cancellationToken);
			engine.TagPrefix = 'A';
			secure = false;

			if (engine.CapabilitiesVersion == 0)
				engine.QueryCapabilities (cancellationToken);

			engine.Disconnected += OnEngineDisconnected;
			OnConnected ();
		}

		static void ComputeDefaultValues (string host, ref int port, ref SecureSocketOptions options, out Uri uri, out bool starttls)
		{
			switch (options) {
			default:
				if (port == 0)
					port = 143;
				break;
			case SecureSocketOptions.Auto:
				switch (port) {
				case 0: port = 143; goto default;
				case 993: options = SecureSocketOptions.SslOnConnect; break;
				default: options = SecureSocketOptions.StartTlsWhenAvailable; break;
				}
				break;
			case SecureSocketOptions.SslOnConnect:
				if (port == 0)
					port = 993;
				break;
			}

			switch (options) {
			case SecureSocketOptions.StartTlsWhenAvailable:
				uri = new Uri ("imap://" + host + ":" + port + "/?starttls=when-available");
				starttls = true;
				break;
			case SecureSocketOptions.StartTls:
				uri = new Uri ("imap://" + host + ":" + port + "/?starttls=always");
				starttls = true;
				break;
			case SecureSocketOptions.SslOnConnect:
				uri = new Uri ("imaps://" + host + ":" + port);
				starttls = false;
				break;
			default:
				uri = new Uri ("imap://" + host + ":" + port);
				starttls = false;
				break;
			}
		}

		/// <summary>
		/// Establish a connection to the specified IMAP server.
		/// </summary>
		/// <remarks>
		/// <para>Establishes a connection to the specified IMAP or IMAP/S server.</para>
		/// <para>If the <paramref name="port"/> has a value of <c>0</c>, then the
		/// <paramref name="options"/> parameter is used to determine the default port to
		/// connect to. The default port used with <see cref="SecureSocketOptions.SslOnConnect"/>
		/// is <c>993</c>. All other values will use a default port of <c>143</c>.</para>
		/// <para>If the <paramref name="options"/> has a value of
		/// <see cref="SecureSocketOptions.Auto"/>, then the <paramref name="port"/> is used
		/// to determine the default security options. If the <paramref name="port"/> has a value
		/// of <c>993</c>, then the default options used will be
		/// <see cref="SecureSocketOptions.SslOnConnect"/>. All other values will use
		/// <see cref="SecureSocketOptions.StartTlsWhenAvailable"/>.</para>
		/// <para>Once a connection is established, properties such as
		/// <see cref="AuthenticationMechanisms"/> and <see cref="Capabilities"/> will be
		/// populated.</para>
		/// </remarks>
		/// <example>
		/// <code language="c#" source="Examples\ImapExamples.cs" region="DownloadMessages"/>
		/// </example>
		/// <param name="host">The host name to connect to.</param>
		/// <param name="port">The port to connect to. If the specified port is <c>0</c>, then the default port will be used.</param>
		/// <param name="options">The secure socket options to when connecting.</param>
		/// <param name="cancellationToken">The cancellation token.</param>
		/// <exception cref="System.ArgumentNullException">
		/// <paramref name="host"/> is <c>null</c>.
		/// </exception>
		/// <exception cref="System.ArgumentOutOfRangeException">
		/// <paramref name="port"/> is not between <c>0</c> and <c>65535</c>.
		/// </exception>
		/// <exception cref="System.ArgumentException">
		/// The <paramref name="host"/> is a zero-length string.
		/// </exception>
		/// <exception cref="System.ObjectDisposedException">
		/// The <see cref="ImapClient"/> has been disposed.
		/// </exception>
		/// <exception cref="System.InvalidOperationException">
		/// The <see cref="ImapClient"/> is already connected.
		/// </exception>
		/// <exception cref="System.NotSupportedException">
		/// <paramref name="options"/> was set to
		/// <see cref="MailKit.Security.SecureSocketOptions.StartTls"/>
		/// and the IMAP server does not support the STARTTLS extension.
		/// </exception>
		/// <exception cref="System.OperationCanceledException">
		/// The operation was canceled via the cancellation token.
		/// </exception>
		/// <exception cref="System.IO.IOException">
		/// An I/O error occurred.
		/// </exception>
		/// <exception cref="ImapProtocolException">
		/// An IMAP protocol error occurred.
		/// </exception>
		public override void Connect (string host, int port = 0, SecureSocketOptions options = SecureSocketOptions.Auto, CancellationToken cancellationToken = default (CancellationToken))
		{
			if (host == null)
				throw new ArgumentNullException ("host");

			if (host.Length == 0)
				throw new ArgumentException ("The host name cannot be empty.", "host");

			if (port < 0 || port > 65535)
				throw new ArgumentOutOfRangeException ("port");

			CheckDisposed ();

			if (IsConnected)
				throw new InvalidOperationException ("The ImapClient is already connected.");

			Stream stream;
			bool starttls;
			Uri uri;

			ComputeDefaultValues (host, ref port, ref options, out uri, out starttls);

#if !NETFX_CORE
#if COREFX
			var ipAddresses = Dns.GetHostAddressesAsync (uri.DnsSafeHost).GetAwaiter ().GetResult ();
#else
			var ipAddresses = Dns.GetHostAddresses (uri.DnsSafeHost);
#endif
			Socket socket = null;

			for (int i = 0; i < ipAddresses.Length; i++) {
				socket = new Socket (ipAddresses[i].AddressFamily, SocketType.Stream, ProtocolType.Tcp);

				try {
					cancellationToken.ThrowIfCancellationRequested ();

					if (LocalEndPoint != null)
						socket.Bind (LocalEndPoint);

					socket.Connect (ipAddresses[i], port);
					break;
				} catch (OperationCanceledException) {
					socket.Dispose ();
					throw;
				} catch {
					socket.Dispose ();

					if (i + 1 == ipAddresses.Length)
						throw;
				}
			}

			if (socket == null)
				throw new IOException (string.Format ("Failed to resolve host: {0}", host));
			
			engine.Uri = uri;

			if (options == SecureSocketOptions.SslOnConnect) {
				var ssl = new SslStream (new NetworkStream (socket, true), false, ValidateRemoteCertificate);
				ssl.AuthenticateAsClient (host, ClientCertificates, SslProtocols, true);
				secure = true;
				stream = ssl;
			} else {
				stream = new NetworkStream (socket, true);
				secure = false;
			}
#else
			var protection = options == SecureSocketOptions.SslOnConnect ? SocketProtectionLevel.Tls12 : SocketProtectionLevel.PlainSocket;
			socket = new StreamSocket ();

			try {
				cancellationToken.ThrowIfCancellationRequested ();
				socket.ConnectAsync (new HostName (host), port.ToString (), protection)
					.AsTask (cancellationToken)
					.GetAwaiter ()
					.GetResult ();
			} catch {
				socket.Dispose ();
				socket = null;
				throw;
			}

			stream = new DuplexStream (socket.InputStream.AsStreamForRead (0), socket.OutputStream.AsStreamForWrite (0));
			engine.Uri = uri;
#endif

			if (stream.CanTimeout) {
				stream.WriteTimeout = timeout;
				stream.ReadTimeout = timeout;
			}

			ProtocolLogger.LogConnect (uri);

			engine.Connect (new ImapStream (stream, socket, ProtocolLogger), cancellationToken);

			try {
				// Only query the CAPABILITIES if the greeting didn't include them.
				if (engine.CapabilitiesVersion == 0)
					engine.QueryCapabilities (cancellationToken);
				
				if (options == SecureSocketOptions.StartTls && (engine.Capabilities & ImapCapabilities.StartTLS) == 0)
					throw new NotSupportedException ("The IMAP server does not support the STARTTLS extension.");
				
				if (starttls && (engine.Capabilities & ImapCapabilities.StartTLS) != 0) {
					var ic = engine.QueueCommand (cancellationToken, null, "STARTTLS\r\n");

					engine.Wait (ic);

					if (ic.Response == ImapCommandResponse.Ok) {
#if !NETFX_CORE
						var tls = new SslStream (stream, false, ValidateRemoteCertificate);
						tls.AuthenticateAsClient (host, ClientCertificates, SslProtocols, true);
						engine.Stream.Stream = tls;
#else
						socket.UpgradeToSslAsync (SocketProtectionLevel.Tls12, new HostName (host))
							.AsTask (cancellationToken)
							.GetAwaiter ()
							.GetResult ();
#endif

						secure = true;

						// Query the CAPABILITIES again if the server did not include an
						// untagged CAPABILITIES response to the STARTTLS command.
						if (engine.CapabilitiesVersion == 1)
							engine.QueryCapabilities (cancellationToken);
					} else if (options == SecureSocketOptions.StartTls) {
						throw ImapCommandException.Create ("STARTTLS", ic);
					}
				}
			} catch {
				engine.Disconnect ();
				secure = false;
				throw;
			}

			engine.Disconnected += OnEngineDisconnected;
			OnConnected ();
		}

#if !NETFX_CORE
		/// <summary>
		/// Establish a connection to the specified IMAP or IMAP/S server using the provided socket.
		/// </summary>
		/// <remarks>
		/// <para>Establishes a connection to the specified IMAP or IMAP/S server using
		/// the provided socket.</para>
		/// <para>If the <paramref name="port"/> has a value of <c>0</c>, then the
		/// <paramref name="options"/> parameter is used to determine the default port to
		/// connect to. The default port used with <see cref="SecureSocketOptions.SslOnConnect"/>
		/// is <c>993</c>. All other values will use a default port of <c>143</c>.</para>
		/// <para>If the <paramref name="options"/> has a value of
		/// <see cref="SecureSocketOptions.Auto"/>, then the <paramref name="port"/> is used
		/// to determine the default security options. If the <paramref name="port"/> has a value
		/// of <c>993</c>, then the default options used will be
		/// <see cref="SecureSocketOptions.SslOnConnect"/>. All other values will use
		/// <see cref="SecureSocketOptions.StartTlsWhenAvailable"/>.</para>
		/// <para>Once a connection is established, properties such as
		/// <see cref="AuthenticationMechanisms"/> and <see cref="Capabilities"/> will be
		/// populated.</para>
		/// </remarks>
		/// <param name="socket">The socket to use for the connection.</param>
		/// <param name="host">The host name to connect to.</param>
		/// <param name="port">The port to connect to. If the specified port is <c>0</c>, then the default port will be used.</param>
		/// <param name="options">The secure socket options to when connecting.</param>
		/// <param name="cancellationToken">The cancellation token.</param>
		/// <exception cref="System.ArgumentNullException">
		/// <para><paramref name="socket"/> is <c>null</c>.</para>
		/// <para>-or-</para>
		/// <para><paramref name="host"/> is <c>null</c>.</para>
		/// </exception>
		/// <exception cref="System.ArgumentOutOfRangeException">
		/// <paramref name="port"/> is not between <c>0</c> and <c>65535</c>.
		/// </exception>
		/// <exception cref="System.ArgumentException">
		/// <para><paramref name="socket"/> is not connected.</para>
		/// <para>-or-</para>
		/// The <paramref name="host"/> is a zero-length string.
		/// </exception>
		/// <exception cref="System.ObjectDisposedException">
		/// The <see cref="ImapClient"/> has been disposed.
		/// </exception>
		/// <exception cref="System.InvalidOperationException">
		/// The <see cref="ImapClient"/> is already connected.
		/// </exception>
		/// <exception cref="System.NotSupportedException">
		/// <paramref name="options"/> was set to
		/// <see cref="MailKit.Security.SecureSocketOptions.StartTls"/>
		/// and the IMAP server does not support the STARTTLS extension.
		/// </exception>
		/// <exception cref="System.OperationCanceledException">
		/// The operation was canceled via the cancellation token.
		/// </exception>
		/// <exception cref="System.IO.IOException">
		/// An I/O error occurred.
		/// </exception>
		/// <exception cref="ImapProtocolException">
		/// An IMAP protocol error occurred.
		/// </exception>
		public void Connect (Socket socket, string host, int port = 0, SecureSocketOptions options = SecureSocketOptions.Auto, CancellationToken cancellationToken = default (CancellationToken))
		{
			if (socket == null)
				throw new ArgumentNullException ("socket");

			if (!socket.Connected)
				throw new ArgumentException ("The socket is not connected.", "socket");

			if (host == null)
				throw new ArgumentNullException ("host");

			if (host.Length == 0)
				throw new ArgumentException ("The host name cannot be empty.", "host");

			if (port < 0 || port > 65535)
				throw new ArgumentOutOfRangeException ("port");

			CheckDisposed ();

			if (IsConnected)
				throw new InvalidOperationException ("The ImapClient is already connected.");
			
			Stream stream;
			bool starttls;
			Uri uri;

			ComputeDefaultValues (host, ref port, ref options, out uri, out starttls);

			engine.Uri = uri;

			if (options == SecureSocketOptions.SslOnConnect) {
				var ssl = new SslStream (new NetworkStream (socket, true), false, ValidateRemoteCertificate);
				ssl.AuthenticateAsClient (host, ClientCertificates, SslProtocols, true);
				secure = true;
				stream = ssl;
			} else {
				stream = new NetworkStream (socket, true);
				secure = false;
			}

			if (stream.CanTimeout) {
				stream.WriteTimeout = timeout;
				stream.ReadTimeout = timeout;
			}

			ProtocolLogger.LogConnect (uri);

			engine.Connect (new ImapStream (stream, socket, ProtocolLogger), cancellationToken);

			try {
				// Only query the CAPABILITIES if the greeting didn't include them.
				if (engine.CapabilitiesVersion == 0)
					engine.QueryCapabilities (cancellationToken);
				
				if (options == SecureSocketOptions.StartTls && (engine.Capabilities & ImapCapabilities.StartTLS) == 0)
					throw new NotSupportedException ("The IMAP server does not support the STARTTLS extension.");
				
				if (starttls && (engine.Capabilities & ImapCapabilities.StartTLS) != 0) {
					var ic = engine.QueueCommand (cancellationToken, null, "STARTTLS\r\n");

					engine.Wait (ic);

					if (ic.Response == ImapCommandResponse.Ok) {
						var tls = new SslStream (stream, false, ValidateRemoteCertificate);
						tls.AuthenticateAsClient (host, ClientCertificates, SslProtocols, true);
						engine.Stream.Stream = tls;

						secure = true;

						// Query the CAPABILITIES again if the server did not include an
						// untagged CAPABILITIES response to the STARTTLS command.
						if (engine.CapabilitiesVersion == 1)
							engine.QueryCapabilities (cancellationToken);
					} else if (options == SecureSocketOptions.StartTls) {
						throw ImapCommandException.Create ("STARTTLS", ic);
					}
				}
			} catch {
				engine.Disconnect ();
				secure = false;
				throw;
			}

			engine.Disconnected += OnEngineDisconnected;
			OnConnected ();
		}
#endif

		/// <summary>
		/// Disconnect the service.
		/// </summary>
		/// <remarks>
		/// If <paramref name="quit"/> is <c>true</c>, a <c>LOGOUT</c> command will be issued in order to disconnect cleanly.
		/// </remarks>
		/// <example>
		/// <code language="c#" source="Examples\ImapExamples.cs" region="DownloadMessages"/>
		/// </example>
		/// <param name="quit">If set to <c>true</c>, a <c>LOGOUT</c> command will be issued in order to disconnect cleanly.</param>
		/// <param name="cancellationToken">The cancellation token.</param>
		/// <exception cref="System.ObjectDisposedException">
		/// The <see cref="ImapClient"/> has been disposed.
		/// </exception>
		public override void Disconnect (bool quit, CancellationToken cancellationToken = default (CancellationToken))
		{
			CheckDisposed ();

			if (!engine.IsConnected)
				return;

			if (quit) {
				try {
					var ic = engine.QueueCommand (cancellationToken, null, "LOGOUT\r\n");

					engine.Wait (ic);
				} catch (OperationCanceledException) {
				} catch (ImapProtocolException) {
				} catch (ImapCommandException) {
				} catch (IOException) {
				}
			}

#if NETFX_CORE
			socket.Dispose ();
			socket = null;
#endif

			engine.Disconnect ();
			secure = false;
		}

#if ENABLE_RECONNECT
		/// <summary>
		/// Reconnect to the most recently connected IMAP server.
		/// </summary>
		/// <remarks>
		/// <para>Reconnects to the most recently connected IMAP server. Once a
		/// successful connection is made, the session will then be re-authenticated
		/// using the account name used in the previous session and the
		/// <paramref name="password"/>.</para>
		/// </remarks>
		/// <param name="password">The password.</param>
		/// <param name="cancellationToken">The cancellation token.</param>
		/// <exception cref="System.ArgumentNullException">
		/// <paramref name="password"/> is <c>null</c>.
		/// </exception>
		/// <exception cref="System.ObjectDisposedException">
		/// The <see cref="ImapClient"/> has been disposed.
		/// </exception>
		/// <exception cref="ServiceNotConnectedException">
		/// The <see cref="ImapClient"/> is not connected.
		/// </exception>
		/// <exception cref="System.InvalidOperationException">
		/// There is no previous session to restore.
		/// </exception>
		/// <exception cref="System.OperationCanceledException">
		/// The operation was canceled via the cancellation token.
		/// </exception>
		/// <exception cref="MailKit.Security.AuthenticationException">
		/// Authentication using the supplied credentials has failed.
		/// </exception>
		/// <exception cref="MailKit.Security.SaslException">
		/// A SASL authentication error occurred.
		/// </exception>
		/// <exception cref="System.NotSupportedException">
		/// The previous session was using the STARTTLS extension but the
		/// IMAP server no longer supports it.
		/// </exception>
		/// <exception cref="System.IO.IOException">
		/// An I/O error occurred.
		/// </exception>
		/// <exception cref="ImapProtocolException">
		/// An IMAP protocol error occurred.
		/// </exception>
		public void Reconnect (string password, CancellationToken cancellationToken = default (CancellationToken))
		{
			if (password == null)
				throw new ArgumentNullException ("password");

			if (identifier == null)
				throw new InvalidOperationException ("There is no previous session to restore.");

			// Note: the identifier has the following syntax: imap(s)://userName@host:port
			int startIndex = identifier.IndexOf (':') + 3;
			int endIndex = identifier.IndexOf ('@');

			var userName = UnescapeUserName (identifier.Substring (startIndex, endIndex - startIndex));

			Connect (engine.Uri, cancellationToken);

			Authenticate (userName, password, cancellationToken);
		}

		/// <summary>
		/// Asynchronously reconnect to the most recently connected IMAP server.
		/// </summary>
		/// <remarks>
		/// <para>Asynchronously reconnects to the most recently connected IMAP server.
		/// Once a successful connection is made, the session will then be
		/// re-authenticated using the account name used in the previous session and
		/// the <paramref name="password"/>.</para>
		/// </remarks>
		/// <param name="password">The password.</param>
		/// <param name="cancellationToken">The cancellation token.</param>
		/// <exception cref="System.ArgumentNullException">
		/// <paramref name="password"/> is <c>null</c>.
		/// </exception>
		/// <exception cref="System.ObjectDisposedException">
		/// The <see cref="ImapClient"/> has been disposed.
		/// </exception>
		/// <exception cref="ServiceNotConnectedException">
		/// The <see cref="ImapClient"/> is not connected.
		/// </exception>
		/// <exception cref="System.InvalidOperationException">
		/// There is no previous session to restore.
		/// </exception>
		/// <exception cref="System.OperationCanceledException">
		/// The operation was canceled via the cancellation token.
		/// </exception>
		/// <exception cref="MailKit.Security.AuthenticationException">
		/// Authentication using the supplied credentials has failed.
		/// </exception>
		/// <exception cref="MailKit.Security.SaslException">
		/// A SASL authentication error occurred.
		/// </exception>
		/// <exception cref="System.NotSupportedException">
		/// The previous session was using the STARTTLS extension but the
		/// IMAP server no longer supports it.
		/// </exception>
		/// <exception cref="System.IO.IOException">
		/// An I/O error occurred.
		/// </exception>
		/// <exception cref="ImapProtocolException">
		/// An IMAP protocol error occurred.
		/// </exception>
		public Task ReconnectAsync (string password, CancellationToken cancellationToken = default (CancellationToken))
		{
			return Task.Factory.StartNew (() => {
				lock (SyncRoot) {
					Reconnect (password, cancellationToken);
				}
			}, cancellationToken, TaskCreationOptions.None, TaskScheduler.Default);
		}
#endif

		/// <summary>
		/// Ping the IMAP server to keep the connection alive.
		/// </summary>
		/// <remarks>
		/// <para>The <c>NOOP</c> command is typically used to keep the connection with the IMAP server
		/// alive. When a client goes too long (typically 30 minutes) without sending any commands to the
		/// IMAP server, the IMAP server will close the connection with the client, forcing the client to
		/// reconnect before it can send any more commands.</para>
		/// <para>The <c>NOOP</c> command also provides a great way for a client to check for new
		/// messages.</para>
		/// <para>When the IMAP server receives a <c>NOOP</c> command, it will reply to the client with a
		/// list of pending updates such as <c>EXISTS</c> and <c>RECENT</c> counts on the currently
		/// selected folder. To receive these notifications, subscribe to the
		/// <see cref="MailFolder.CountChanged"/> and <see cref="MailFolder.RecentChanged"/> events,
		/// respectively.</para>
		/// <para>For more information about the <c>NOOP</c> command, see
		/// <a href="https://tools.ietf.org/html/rfc3501#section-6.1.2">rfc3501</a>.</para>
		/// </remarks>
		/// <example>
		/// <code language="c#" source="Examples\ImapIdleExample.cs"/>
		/// </example>
		/// <param name="cancellationToken">The cancellation token.</param>
		/// <exception cref="System.ObjectDisposedException">
		/// The <see cref="ImapClient"/> has been disposed.
		/// </exception>
		/// <exception cref="ServiceNotConnectedException">
		/// The <see cref="ImapClient"/> is not connected.
		/// </exception>
		/// <exception cref="ServiceNotAuthenticatedException">
		/// The <see cref="ImapClient"/> is not authenticated.
		/// </exception>
		/// <exception cref="System.OperationCanceledException">
		/// The operation was canceled via the cancellation token.
		/// </exception>
		/// <exception cref="System.IO.IOException">
		/// An I/O error occurred.
		/// </exception>
		/// <exception cref="ImapCommandException">
		/// The server replied to the NOOP command with a NO or BAD response.
		/// </exception>
		/// <exception cref="ImapProtocolException">
		/// The server responded with an unexpected token.
		/// </exception>
		public override void NoOp (CancellationToken cancellationToken = default (CancellationToken))
		{
			CheckDisposed ();
			CheckConnected ();
			CheckAuthenticated ();

			var ic = engine.QueueCommand (cancellationToken, null, "NOOP\r\n");

			engine.Wait (ic);

			if (ic.Response != ImapCommandResponse.Ok)
				throw ImapCommandException.Create ("NOOP", ic);
		}

		static void IdleComplete (object state)
		{
			var ctx = (ImapIdleContext) state;

			if (ctx.Engine.State == ImapEngineState.Idle) {
				var buf = Encoding.ASCII.GetBytes ("DONE\r\n");

				ctx.Engine.Stream.Write (buf, 0, buf.Length);
				ctx.Engine.Stream.Flush ();

				ctx.Engine.State = ImapEngineState.Selected;
			}
		}

		/// <summary>
		/// Toggle the <see cref="ImapClient"/> into the IDLE state.
		/// </summary>
		/// <remarks>
		/// <para>When a client enters the IDLE state, the IMAP server will send
		/// events to the client as they occur on the selected folder. These events
		/// may include notifications of new messages arriving, expunge notifications,
		/// flag changes, etc.</para>
		/// <para>Due to the nature of the IDLE command, a folder must be selected
		/// before a client can enter into the IDLE state. This can be done by
		/// opening a folder using
		/// <see cref="MailKit.MailFolder.Open(FolderAccess,System.Threading.CancellationToken)"/>
		/// or any of the other variants.</para>
		/// <para>While the IDLE command is running, no other commands may be issued until the
		/// <paramref name="doneToken"/> is cancelled.</para>
		/// <note type="note">It is especially important to cancel the <paramref name="doneToken"/>
		/// before cancelling the <paramref name="cancellationToken"/> when using SSL or TLS due to
		/// the fact that <see cref="System.Net.Security.SslStream"/> cannot be polled.</note>
		/// </remarks>
		/// <example>
		/// <code language="c#" source="Examples\ImapIdleExample.cs"/>
		/// </example>
		/// <param name="doneToken">The cancellation token used to return to the non-idle state.</param>
		/// <param name="cancellationToken">The cancellation token.</param>
		/// <exception cref="System.ArgumentException">
		/// <paramref name="doneToken"/> must be cancellable (i.e. <see cref="System.Threading.CancellationToken.None"/> cannot be used).
		/// </exception>
		/// <exception cref="System.ObjectDisposedException">
		/// The <see cref="ImapClient"/> has been disposed.
		/// </exception>
		/// <exception cref="ServiceNotConnectedException">
		/// The <see cref="ImapClient"/> is not connected.
		/// </exception>
		/// <exception cref="ServiceNotAuthenticatedException">
		/// The <see cref="ImapClient"/> is not authenticated.
		/// </exception>
		/// <exception cref="System.InvalidOperationException">
		/// A <see cref="ImapFolder"/> has not been opened.
		/// </exception>
		/// <exception cref="System.NotSupportedException">
		/// The IMAP server does not support the IDLE extension.
		/// </exception>
		/// <exception cref="System.OperationCanceledException">
		/// The operation was canceled via the cancellation token.
		/// </exception>
		/// <exception cref="System.IO.IOException">
		/// An I/O error occurred.
		/// </exception>
		/// <exception cref="ImapCommandException">
		/// The server replied to the IDLE command with a NO or BAD response.
		/// </exception>
		/// <exception cref="ImapProtocolException">
		/// The server responded with an unexpected token.
		/// </exception>
		public void Idle (CancellationToken doneToken, CancellationToken cancellationToken = default (CancellationToken))
		{
			if (!doneToken.CanBeCanceled)
				throw new ArgumentException ("The doneToken must be cancellable.", "doneToken");

			CheckDisposed ();
			CheckConnected ();
			CheckAuthenticated ();

			if ((engine.Capabilities & ImapCapabilities.Idle) == 0)
				throw new NotSupportedException ("The IMAP server does not support the IDLE extension.");

			if (engine.State != ImapEngineState.Selected)
				throw new InvalidOperationException ("An ImapFolder has not been opened.");

			using (var context = new ImapIdleContext (engine, doneToken, cancellationToken)) {
				var ic = engine.QueueCommand (cancellationToken, null, "IDLE\r\n");
				ic.UserData = context;

				ic.ContinuationHandler = (imap, cmd, text) => {
					imap.State = ImapEngineState.Idle;

					doneToken.Register (IdleComplete, context);
				};

				engine.Wait (ic);

				if (ic.Response != ImapCommandResponse.Ok)
					throw ImapCommandException.Create ("IDLE", ic);
			}
		}

		/// <summary>
		/// Asynchronously toggle the <see cref="ImapClient"/> into the IDLE state.
		/// </summary>
		/// <remarks>
		/// <para>When a client enters the IDLE state, the IMAP server will send
		/// events to the client as they occur on the selected folder. These events
		/// may include notifications of new messages arriving, expunge notifications,
		/// flag changes, etc.</para>
		/// <para>Due to the nature of the IDLE command, a folder must be selected
		/// before a client can enter into the IDLE state. This can be done by
		/// opening a folder using
		/// <see cref="MailKit.MailFolder.Open(FolderAccess,System.Threading.CancellationToken)"/>
		/// or any of the other variants.</para>
		/// <para>While the IDLE command is running, no other commands may be issued until the
		/// <paramref name="doneToken"/> is cancelled.</para>
		/// <note type="note">It is especially important to cancel the <paramref name="doneToken"/>
		/// before cancelling the <paramref name="cancellationToken"/> when using SSL or TLS due to
		/// the fact that <see cref="System.Net.Security.SslStream"/> cannot be polled.</note>
		/// </remarks>
		/// <returns>An asynchronous task context.</returns>
		/// <param name="doneToken">The cancellation token used to return to the non-idle state.</param>
		/// <param name="cancellationToken">The cancellation token.</param>
		/// <exception cref="System.ArgumentException">
		/// <paramref name="doneToken"/> must be cancellable (i.e. <see cref="System.Threading.CancellationToken.None"/> cannot be used).
		/// </exception>
		/// <exception cref="System.ObjectDisposedException">
		/// The <see cref="ImapClient"/> has been disposed.
		/// </exception>
		/// <exception cref="ServiceNotConnectedException">
		/// The <see cref="ImapClient"/> is not connected.
		/// </exception>
		/// <exception cref="ServiceNotAuthenticatedException">
		/// The <see cref="ImapClient"/> is not authenticated.
		/// </exception>
		/// <exception cref="System.InvalidOperationException">
		/// A <see cref="ImapFolder"/> has not been opened.
		/// </exception>
		/// <exception cref="System.NotSupportedException">
		/// The IMAP server does not support the IDLE extension.
		/// </exception>
		/// <exception cref="System.OperationCanceledException">
		/// The operation was canceled via the cancellation token.
		/// </exception>
		/// <exception cref="System.IO.IOException">
		/// An I/O error occurred.
		/// </exception>
		/// <exception cref="ImapCommandException">
		/// The server replied to the IDLE command with a NO or BAD response.
		/// </exception>
		/// <exception cref="ImapProtocolException">
		/// The server responded with an unexpected token.
		/// </exception>
		public Task IdleAsync (CancellationToken doneToken, CancellationToken cancellationToken = default (CancellationToken))
		{
			if (!doneToken.CanBeCanceled)
				throw new ArgumentException ("The doneToken must be cancellable.", "doneToken");

			CheckDisposed ();
			CheckConnected ();
			CheckAuthenticated ();

			if ((engine.Capabilities & ImapCapabilities.Idle) == 0)
				throw new NotSupportedException ("The IMAP server does not support the IDLE extension.");

			if (engine.State != ImapEngineState.Selected)
				throw new InvalidOperationException ("An ImapFolder has not been opened.");

			return Task.Factory.StartNew (() => {
				lock (SyncRoot) {
					Idle (doneToken, cancellationToken);
				}
			}, cancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default);
		}

		#endregion

		#region IMailStore implementation

		/// <summary>
		/// Get the personal namespaces.
		/// </summary>
		/// <remarks>
		/// The personal folder namespaces contain a user's personal mailbox folders.
		/// </remarks>
		/// <value>The personal namespaces.</value>
		public override FolderNamespaceCollection PersonalNamespaces {
			get { return engine.PersonalNamespaces; }
		}

		/// <summary>
		/// Get the shared namespaces.
		/// </summary>
		/// <remarks>
		/// The shared folder namespaces contain mailbox folders that are shared with the user.
		/// </remarks>
		/// <value>The shared namespaces.</value>
		public override FolderNamespaceCollection SharedNamespaces {
			get { return engine.SharedNamespaces; }
		}

		/// <summary>
		/// Get the other namespaces.
		/// </summary>
		/// <remarks>
		/// The other folder namespaces contain other mailbox folders.
		/// </remarks>
		/// <value>The other namespaces.</value>
		public override FolderNamespaceCollection OtherNamespaces {
			get { return engine.OtherNamespaces; }
		}

		/// <summary>
		/// Get whether or not the mail store supports quotas.
		/// </summary>
		/// <remarks>
		/// Gets whether or not the mail store supports quotas.
		/// </remarks>
		/// <value><c>true</c> if the mail store supports quotas; otherwise, <c>false</c>.</value>
		public override bool SupportsQuotas {
			get { return (engine.Capabilities & ImapCapabilities.Quota) != 0; }
		}

		/// <summary>
		/// Get the Inbox folder.
		/// </summary>
		/// <remarks>
		/// <para>The Inbox folder is the default folder and always exists on the server.</para>
		/// <note type="note">This property will only be available after the client has been authenticated.</note>
		/// </remarks>
		/// <example>
		/// <code language="c#" source="Examples\ImapExamples.cs" region="DownloadMessages"/>
		/// </example>
		/// <value>The Inbox folder.</value>
		/// <exception cref="System.ObjectDisposedException">
		/// The <see cref="ImapClient"/> has been disposed.
		/// </exception>
		/// <exception cref="ServiceNotConnectedException">
		/// The <see cref="ImapClient"/> is not connected.
		/// </exception>
		/// <exception cref="ServiceNotAuthenticatedException">
		/// The <see cref="ImapClient"/> is not authenticated.
		/// </exception>
		public override IMailFolder Inbox {
			get {
				CheckDisposed ();
				CheckConnected ();
				CheckAuthenticated ();

				return engine.Inbox;
			}
		}

		/// <summary>
		/// Get the specified special folder.
		/// </summary>
		/// <remarks>
		/// Not all IMAP servers support special folders. Only IMAP servers
		/// supporting the <see cref="ImapCapabilities.SpecialUse"/> or
		/// <see cref="ImapCapabilities.XList"/> extensions may have
		/// special folders.
		/// </remarks>
		/// <returns>The folder if available; otherwise <c>null</c>.</returns>
		/// <param name="folder">The type of special folder.</param>
		/// <exception cref="System.ArgumentOutOfRangeException">
		/// <paramref name="folder"/> is out of range.
		/// </exception>
		/// <exception cref="System.ObjectDisposedException">
		/// The <see cref="ImapClient"/> has been disposed.
		/// </exception>
		/// <exception cref="ServiceNotConnectedException">
		/// The <see cref="ImapClient"/> is not connected.
		/// </exception>
		/// <exception cref="ServiceNotAuthenticatedException">
		/// The <see cref="ImapClient"/> is not authenticated.
		/// </exception>
		/// <exception cref="System.NotSupportedException">
		/// The IMAP server does not support the SPECIAL-USE nor XLIST extensions.
		/// </exception>
		public override IMailFolder GetFolder (SpecialFolder folder)
		{
			CheckDisposed ();
			CheckConnected ();
			CheckAuthenticated ();

			if ((Capabilities & (ImapCapabilities.SpecialUse | ImapCapabilities.XList)) == 0)
				throw new NotSupportedException ("The IMAP server does not support the SPECIAL-USE nor XLIST extensions.");

			switch (folder) {
			case SpecialFolder.All:     return engine.All;
			case SpecialFolder.Archive: return engine.Archive;
			case SpecialFolder.Drafts:  return engine.Drafts;
			case SpecialFolder.Flagged: return engine.Flagged;
			case SpecialFolder.Junk:    return engine.Junk;
			case SpecialFolder.Sent:    return engine.Sent;
			case SpecialFolder.Trash:   return engine.Trash;
			default: throw new ArgumentOutOfRangeException ("folder");
			}
		}

		/// <summary>
		/// Get the folder for the specified namespace.
		/// </summary>
		/// <remarks>
		/// Gets the folder for the specified namespace.
		/// </remarks>
		/// <returns>The folder.</returns>
		/// <param name="namespace">The namespace.</param>
		/// <exception cref="System.ArgumentNullException">
		/// <paramref name="namespace"/> is <c>null</c>.
		/// </exception>
		/// <exception cref="System.ObjectDisposedException">
		/// The <see cref="ImapClient"/> has been disposed.
		/// </exception>
		/// <exception cref="ServiceNotConnectedException">
		/// The <see cref="ImapClient"/> is not connected.
		/// </exception>
		/// <exception cref="ServiceNotAuthenticatedException">
		/// The <see cref="ImapClient"/> is not authenticated.
		/// </exception>
		/// <exception cref="FolderNotFoundException">
		/// The folder could not be found.
		/// </exception>
		public override IMailFolder GetFolder (FolderNamespace @namespace)
		{
			if (@namespace == null)
				throw new ArgumentNullException ("namespace");

			CheckDisposed ();
			CheckConnected ();
			CheckAuthenticated ();

			var encodedName = engine.EncodeMailboxName (@namespace.Path);
			ImapFolder folder;

			if (engine.GetCachedFolder (encodedName, out folder))
				return folder;

			throw new FolderNotFoundException (@namespace.Path);
		}

		/// <summary>
		/// Get all of the folders within the specified namespace.
		/// </summary>
		/// <remarks>
		/// Gets all of the folders within the specified namespace.
		/// </remarks>
		/// <returns>The folders.</returns>
		/// <param name="namespace">The namespace.</param>
		/// <param name="items">The status items to pre-populate.</param>
		/// <param name="subscribedOnly">If set to <c>true</c>, only subscribed folders will be listed.</param>
		/// <param name="cancellationToken">The cancellation token.</param>
		/// <exception cref="System.ArgumentNullException">
		/// <paramref name="namespace"/> is <c>null</c>.
		/// </exception>
		/// <exception cref="System.ObjectDisposedException">
		/// The <see cref="ImapClient"/> has been disposed.
		/// </exception>
		/// <exception cref="ServiceNotConnectedException">
		/// The <see cref="ImapClient"/> is not connected.
		/// </exception>
		/// <exception cref="ServiceNotAuthenticatedException">
		/// The <see cref="ImapClient"/> is not authenticated.
		/// </exception>
		/// <exception cref="System.OperationCanceledException">
		/// The operation was canceled via the cancellation token.
		/// </exception>
		/// <exception cref="FolderNotFoundException">
		/// The namespace folder could not be found.
		/// </exception>
		/// <exception cref="System.IO.IOException">
		/// An I/O error occurred.
		/// </exception>
		/// <exception cref="ImapCommandException">
		/// The server replied to the LIST or LSUB command with a NO or BAD response.
		/// </exception>
		/// <exception cref="ImapProtocolException">
		/// The server responded with an unexpected token.
		/// </exception>
		public override IEnumerable<IMailFolder> GetFolders (FolderNamespace @namespace, StatusItems items = StatusItems.None, bool subscribedOnly = false, CancellationToken cancellationToken = default (CancellationToken))
		{
			if (@namespace == null)
				throw new ArgumentNullException ("namespace");

			CheckDisposed ();
			CheckConnected ();
			CheckAuthenticated ();

			foreach (var folder in engine.GetFolders (@namespace, items, subscribedOnly, cancellationToken))
				yield return folder;

			yield break;
		}

		/// <summary>
		/// Get the folder for the specified path.
		/// </summary>
		/// <remarks>
		/// Gets the folder for the specified path.
		/// </remarks>
		/// <returns>The folder.</returns>
		/// <param name="path">The folder path.</param>
		/// <param name="cancellationToken">The cancellation token.</param>
		/// <exception cref="System.ArgumentNullException">
		/// <paramref name="path"/> is <c>null</c>.
		/// </exception>
		/// <exception cref="System.ObjectDisposedException">
		/// The <see cref="ImapClient"/> has been disposed.
		/// </exception>
		/// <exception cref="ServiceNotConnectedException">
		/// The <see cref="ImapClient"/> is not connected.
		/// </exception>
		/// <exception cref="ServiceNotAuthenticatedException">
		/// The <see cref="ImapClient"/> is not authenticated.
		/// </exception>
		/// <exception cref="System.OperationCanceledException">
		/// The operation was canceled via the cancellation token.
		/// </exception>
		/// <exception cref="FolderNotFoundException">
		/// The folder could not be found.
		/// </exception>
		/// <exception cref="System.IO.IOException">
		/// An I/O error occurred.
		/// </exception>
		/// <exception cref="ImapCommandException">
		/// The server replied to the IDLE command with a NO or BAD response.
		/// </exception>
		/// <exception cref="ImapProtocolException">
		/// The server responded with an unexpected token.
		/// </exception>
		public override IMailFolder GetFolder (string path, CancellationToken cancellationToken = default (CancellationToken))
		{
			if (path == null)
				throw new ArgumentNullException ("path");

			CheckDisposed ();
			CheckConnected ();
			CheckAuthenticated ();

			return engine.GetFolder (path, cancellationToken);
		}

		#endregion

		void OnEngineAlert (object sender, AlertEventArgs e)
		{
			OnAlert (e);
		}

		void OnEngineDisconnected (object sender, EventArgs e)
		{
			engine.Disconnected -= OnEngineDisconnected;
			OnDisconnected ();
			secure = false;
		}

		/// <summary>
		/// Releases the unmanaged resources used by the <see cref="ImapClient"/> and
		/// optionally releases the managed resources.
		/// </summary>
		/// <remarks>
		/// Releases the unmanaged resources used by the <see cref="ImapClient"/> and
		/// optionally releases the managed resources.
		/// </remarks>
		/// <param name="disposing"><c>true</c> to release both managed and unmanaged resources;
		/// <c>false</c> to release only the unmanaged resources.</param>
		protected override void Dispose (bool disposing)
		{
			if (disposing && !disposed) {
				engine.Dispose ();

#if NETFX_CORE
				if (socket != null)
					socket.Dispose ();
#endif

				disposed = true;
			}

			base.Dispose (disposing);
		}
	}
}
