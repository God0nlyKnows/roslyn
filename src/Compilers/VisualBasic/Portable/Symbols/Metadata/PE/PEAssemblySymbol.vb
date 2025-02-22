﻿' Licensed to the .NET Foundation under one or more agreements.
' The .NET Foundation licenses this file to you under the MIT license.
' See the LICENSE file in the project root for more information.

Imports System.Collections.Generic
Imports System.Collections.Immutable
Imports System.Collections.ObjectModel
Imports System.Linq.Enumerable
Imports Microsoft.CodeAnalysis.Text
Imports Microsoft.CodeAnalysis.VisualBasic.Symbols
Imports Microsoft.CodeAnalysis.VisualBasic.Syntax
Imports System.Runtime.InteropServices
Imports System.Reflection.Metadata.Ecma335
Imports System.Threading

Namespace Microsoft.CodeAnalysis.VisualBasic.Symbols.Metadata.PE

    ''' <summary>
    ''' Represents an assembly imported from a PE.
    ''' </summary>
    ''' <remarks></remarks>
    Friend NotInheritable Class PEAssemblySymbol
        Inherits MetadataOrSourceAssemblySymbol

        ''' <summary>
        ''' An Assembly object providing metadata for the assembly.
        ''' </summary>
        ''' <remarks></remarks>
        Private ReadOnly _assembly As PEAssembly

        ''' <summary>
        ''' A MetadataDocumentationProvider providing XML documentation for this assembly.
        ''' </summary>
        Private ReadOnly _documentationProvider As DocumentationProvider

        ''' <summary>
        ''' The list of contained PEModuleSymbol objects.
        ''' The list doesn't use type ReadOnlyCollection(Of PEModuleSymbol) so that we
        ''' can return it from Modules property as is.
        ''' </summary>
        ''' <remarks></remarks>
        Private ReadOnly _modules As ImmutableArray(Of ModuleSymbol)

        ''' <summary>
        ''' An array of assemblies involved in canonical type resolution of
        ''' NoPia local types defined within this assembly. In other words, all 
        ''' references used by a compilation referencing this assembly.
        ''' The array and its content is provided by ReferenceManager and must not be modified.
        ''' </summary>
        Private _noPiaResolutionAssemblies As ImmutableArray(Of AssemblySymbol)

        ''' <summary>
        ''' An array of assemblies referenced by this assembly, which are linked (/l-ed) by 
        ''' each compilation that is using this AssemblySymbol as a reference. 
        ''' If this AssemblySymbol is linked too, it will be in this array too.
        ''' The array and its content is provided by ReferenceManager and must not be modified.
        ''' </summary>
        Private _linkedReferencedAssemblies As ImmutableArray(Of AssemblySymbol)

        ''' <summary>
        ''' Assembly is /l-ed by compilation that is using it as a reference.
        ''' </summary>
        Private ReadOnly _isLinked As Boolean

        Private _lazyMightContainExtensionMethods As Byte = ThreeState.Unknown

        Private _lazyCustomAttributes As ImmutableArray(Of VisualBasicAttributeData)

        Private _lazyCachedCompilerFeatureRequiredDiagnosticInfo As DiagnosticInfo = ErrorFactory.EmptyDiagnosticInfo

        Friend Sub New(assembly As PEAssembly, documentationProvider As DocumentationProvider,
                       isLinked As Boolean, importOptions As MetadataImportOptions)
            Debug.Assert(assembly IsNot Nothing)
            _assembly = assembly

            _documentationProvider = documentationProvider

            Dim modules(assembly.Modules.Length - 1) As ModuleSymbol

            For i As Integer = 0 To assembly.Modules.Length - 1 Step 1
                modules(i) = New PEModuleSymbol(Me, assembly.Modules(i), importOptions, i)
            Next

            _modules = modules.AsImmutableOrNull()
            _isLinked = isLinked
        End Sub

        Friend ReadOnly Property Assembly As PEAssembly
            Get
                Return _assembly
            End Get
        End Property

        Public Overrides ReadOnly Property Identity As AssemblyIdentity
            Get
                Return _assembly.Identity
            End Get
        End Property

        Public Overrides ReadOnly Property AssemblyVersionPattern As Version
            Get
                ' TODO: https://github.com/dotnet/roslyn/issues/9000
                Return Nothing
            End Get
        End Property

        Friend Overrides ReadOnly Property PublicKey As ImmutableArray(Of Byte)
            Get
                Return _assembly.Identity.PublicKey
            End Get
        End Property

        Friend Overrides Function GetGuidString(ByRef guidString As String) As Boolean
            Return Assembly.Modules(0).HasGuidAttribute(Assembly.Handle, guidString)
        End Function

        Friend Overrides Function AreInternalsVisibleToThisAssembly(potentialGiverOfAccess As AssemblySymbol) As Boolean
            Return MakeFinalIVTDetermination(potentialGiverOfAccess) = IVTConclusion.Match
        End Function

        Friend Overrides Function GetInternalsVisibleToPublicKeys(simpleName As String) As IEnumerable(Of ImmutableArray(Of Byte))
            Return Assembly.GetInternalsVisibleToPublicKeys(simpleName)
        End Function

        Friend Overrides Function GetInternalsVisibleToAssemblyNames() As IEnumerable(Of String)
            Return Assembly.GetInternalsVisibleToAssemblyNames()
        End Function

        Public Overloads Overrides Function GetAttributes() As ImmutableArray(Of VisualBasicAttributeData)
            If _lazyCustomAttributes.IsDefault Then
                PrimaryModule.LoadCustomAttributes(Me.Assembly.Handle, _lazyCustomAttributes)
            End If
            Return _lazyCustomAttributes
        End Function

        Public Overrides ReadOnly Property Locations As ImmutableArray(Of Location)
            Get
                Return StaticCast(Of Location).From(PrimaryModule.MetadataLocation)
            End Get
        End Property

        Public Overrides ReadOnly Property MetadataToken As Integer
            Get
                Return MetadataTokens.GetToken(_assembly.Handle)
            End Get
        End Property

        Public Overrides ReadOnly Property Modules As ImmutableArray(Of ModuleSymbol)
            Get
                Return _modules
            End Get
        End Property

        Friend ReadOnly Property PrimaryModule As PEModuleSymbol
            Get
                Return DirectCast(Me.Modules(0), PEModuleSymbol)
            End Get
        End Property

        ''' <summary>
        ''' Look up the assemblies to which the given metadata type Is forwarded.
        ''' </summary>
        ''' <param name="emittedName"></param>
        ''' <param name="ignoreCase">Pass true to look up fullName case-insensitively.  WARNING: more expensive.</param>
        ''' <param name="matchedName">Returns the actual casing of the matching name.</param>
        ''' <returns>
        ''' The assemblies to which the given type Is forwarded Or null, if there isn't one.
        ''' </returns>
        ''' <remarks>
        ''' The returned assemblies may also forward the type.
        ''' </remarks>
        Friend Function LookupAssembliesForForwardedMetadataType(ByRef emittedName As MetadataTypeName, ignoreCase As Boolean, <Out> ByRef matchedName As String) As (FirstSymbol As AssemblySymbol, SecondSymbol As AssemblySymbol)
            ' Look in the type forwarders of the primary module of this assembly, clr does not honor type forwarder		
            ' in non-primary modules.

            ' Examine the type forwarders, but only from the primary module.
            Return PrimaryModule.GetAssembliesForForwardedType(emittedName, ignoreCase, matchedName)
        End Function

        Friend Overrides Function GetAllTopLevelForwardedTypes() As IEnumerable(Of NamedTypeSymbol)
            Return PrimaryModule.GetForwardedTypes()
        End Function

        Friend Overrides Function TryLookupForwardedMetadataTypeWithCycleDetection(ByRef emittedName As MetadataTypeName, visitedAssemblies As ConsList(Of AssemblySymbol), ignoreCase As Boolean) As NamedTypeSymbol
            ' Check if it is a forwarded type.
            Dim matchedName As String = Nothing
            Dim forwardedToAssemblies = LookupAssembliesForForwardedMetadataType(emittedName, ignoreCase, matchedName)

            If forwardedToAssemblies.FirstSymbol IsNot Nothing Then
                If forwardedToAssemblies.SecondSymbol IsNot Nothing Then
                    ' Report the main module as that is the only one checked. clr does not honor type forwarders in non-primary modules.
                    Return CreateMultipleForwardingErrorTypeSymbol(emittedName, PrimaryModule, forwardedToAssemblies.FirstSymbol, forwardedToAssemblies.SecondSymbol)
                End If

                ' Don't bother to check the forwarded-to assembly if we've already seen it.
                If visitedAssemblies IsNot Nothing AndAlso visitedAssemblies.Contains(forwardedToAssemblies.FirstSymbol) Then
                    Return CreateCycleInTypeForwarderErrorTypeSymbol(emittedName)
                Else
                    visitedAssemblies = New ConsList(Of AssemblySymbol)(Me, If(visitedAssemblies, ConsList(Of AssemblySymbol).Empty))

                    If ignoreCase AndAlso Not String.Equals(emittedName.FullName, matchedName, StringComparison.Ordinal) Then
                        emittedName = MetadataTypeName.FromFullName(matchedName, emittedName.UseCLSCompliantNameArityEncoding, emittedName.ForcedArity)
                    End If

                    Return forwardedToAssemblies.FirstSymbol.LookupDeclaredOrForwardedTopLevelMetadataType(emittedName, visitedAssemblies)
                End If
            End If

            Return Nothing
        End Function

        Friend Overrides Function GetNoPiaResolutionAssemblies() As ImmutableArray(Of AssemblySymbol)
            Return _noPiaResolutionAssemblies
        End Function

        Friend Overrides Sub SetNoPiaResolutionAssemblies(assemblies As ImmutableArray(Of AssemblySymbol))
            _noPiaResolutionAssemblies = assemblies
        End Sub

        Friend Overrides Sub SetLinkedReferencedAssemblies(assemblies As ImmutableArray(Of AssemblySymbol))
            _linkedReferencedAssemblies = assemblies
        End Sub

        Friend Overrides Function GetLinkedReferencedAssemblies() As ImmutableArray(Of AssemblySymbol)
            Return _linkedReferencedAssemblies
        End Function

        Friend Overrides ReadOnly Property IsLinked As Boolean
            Get
                Return _isLinked
            End Get
        End Property

        Friend ReadOnly Property DocumentationProvider As DocumentationProvider
            Get
                Return _documentationProvider
            End Get
        End Property

        Public Overrides ReadOnly Property MightContainExtensionMethods As Boolean
            Get
                If _lazyMightContainExtensionMethods = ThreeState.Unknown Then
                    Dim primaryModuleSymbol = PrimaryModule
                    If primaryModuleSymbol.Module.HasExtensionAttribute(_assembly.Handle, ignoreCase:=True) Then
                        _lazyMightContainExtensionMethods = ThreeState.True
                    Else
                        _lazyMightContainExtensionMethods = ThreeState.False
                    End If
                End If

                Return _lazyMightContainExtensionMethods = ThreeState.True
            End Get
        End Property

        ''' <remarks>
        ''' This is for perf, not for correctness.
        ''' </remarks>
        Friend Overrides ReadOnly Property DeclaringCompilation As VisualBasicCompilation
            Get
                Return Nothing
            End Get
        End Property

        Public Overrides Function GetMetadata() As AssemblyMetadata
            Return _assembly.GetNonDisposableMetadata()
        End Function

        Friend Function GetCompilerFeatureRequiredDiagnosticInfo() As DiagnosticInfo
            If _lazyCachedCompilerFeatureRequiredDiagnosticInfo Is ErrorFactory.EmptyDiagnosticInfo Then
                Interlocked.CompareExchange(
                    _lazyCachedCompilerFeatureRequiredDiagnosticInfo,
                    DeriveCompilerFeatureRequiredAttributeDiagnostic(Me, PrimaryModule, _assembly.Handle, CompilerFeatureRequiredFeatures.None, New MetadataDecoder(PrimaryModule)),
                    ErrorFactory.EmptyDiagnosticInfo)
            End If

            Return _lazyCachedCompilerFeatureRequiredDiagnosticInfo
        End Function

        Public Overrides ReadOnly Property HasUnsupportedMetadata As Boolean
            Get
                Dim info = GetCompilerFeatureRequiredDiagnosticInfo()
                If info IsNot Nothing Then
                    Return info.Code = DirectCast(ERRID.ERR_UnsupportedCompilerFeature, Integer) OrElse MyBase.HasUnsupportedMetadata
                End If

                Return MyBase.HasUnsupportedMetadata
            End Get
        End Property
    End Class
End Namespace
