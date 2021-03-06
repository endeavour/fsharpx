﻿// Copyright (c) Microsoft Corporation 2005-2012.
// This sample code is provided "as is" without warranty of any kind. 
// We disclaim all warranties, either express or implied, including the 
// warranties of merchantability and fitness for a particular purpose. 

/// Provides Freebase schema and data as provided types, properties and methods
namespace FSharpx.TypeProviders.Freebase.FreebaseTypeExtender

open System
open System.Collections.Generic
open System.Reflection
open System.Linq
open Microsoft.FSharp.Core.CompilerServices
open Microsoft.FSharp.Quotations
open Samples.FSharp.ProvidedTypes
open FSharpx.TypeProviders.Freebase.FreebaseRequests
open FSharpx.TypeProviders.Freebase.FreebaseReflection
open Utilities.Caching

#if BROWSER
module AsyncUtilities = 
    let SwitchToDispatcher() : Async<unit> = 
        if System.Windows.Deployment.Current.Dispatcher.CheckAccess() then async.Return()
        else Async.FromContinuations(fun (scont,_,_) -> do System.Windows.Deployment.Current.Dispatcher.BeginInvoke(System.Action< >(fun () -> scont())) |> ignore)

    let RunOnMainThread(f)= 
        async { do! SwitchToDispatcher()
                return f() }
         |> Async.RunSynchronously
#endif

module List = 
    let headAndTail l = match l with [] -> invalidArg "l" "empty list" | h::t -> (h,t)
    let frontAndBack l = 
        let rec loop acc l = 
            match l with
            | [] -> invalidArg "l" "empty list" 
            | [h] -> List.rev acc,h
            | h::t -> loop  (h::acc) t
        loop [] l

/// Find the handles in the Freebase type provider runtime DLL. 
type internal FreebaseRuntimeInfo (config : TypeProviderConfig) =
#if FX_NO_ASSEMBLY_LOAD_FROM
    let onUiThread f = 
        if System.Windows.Deployment.Current.Dispatcher.CheckAccess() then 
            f() 
        else
            let resultTask = System.Threading.Tasks.TaskCompletionSource<'T>()
            System.Windows.Deployment.Current.Dispatcher.BeginInvoke(Action(fun () -> try resultTask.SetResult (f()) with err -> resultTask.SetException err)) |> ignore
            resultTask.Task.Result

    let runtimeAssembly = 
        onUiThread (fun () ->
            let assemblyPart = System.Windows.AssemblyPart()
            let FileStreamReadShim(fileName) = 
                match System.Windows.Application.GetResourceStream(System.Uri(fileName,System.UriKind.Relative)) with 
                | null -> System.IO.IsolatedStorage.IsolatedStorageFile.GetUserStoreForApplication().OpenFile(fileName, System.IO.FileMode.Open) :> System.IO.Stream 
                | resStream -> resStream.Stream
            let assemblyStream = FileStreamReadShim config.RuntimeAssembly
            
            assemblyPart.Load(assemblyStream))
#else
    let runtimeAssembly = Assembly.LoadFrom(config.RuntimeAssembly)
#endif

    member val FreebaseDataContextType = runtimeAssembly.GetType("FSharpx.TypeProviders.Freebase.FreebaseRuntime.FreebaseDataContext")
    member val FreebaseIndividualsType = runtimeAssembly.GetType("FSharpx.TypeProviders.Freebase.FreebaseRuntime.FreebaseIndividuals")
    member val FreebaseObjectType = runtimeAssembly.GetType("FSharpx.TypeProviders.Freebase.FreebaseRuntime.IFreebaseObject")
    member val FreebaseDomainType = runtimeAssembly.GetType("FSharpx.TypeProviders.Freebase.FreebaseRuntime.FreebaseDomain")
    member val FreebaseDomainCategoryType = runtimeAssembly.GetType("FSharpx.TypeProviders.Freebase.FreebaseRuntime.FreebaseDomainCategory")

    member this.RuntimeAssembly = runtimeAssembly

type internal DomainId = KnownDomain of string | UnknownDomain

/// This is the Freebase type provider.    
[<Microsoft.FSharp.Core.CompilerServices.TypeProvider>]
type public FreebaseTypeProvider(config : TypeProviderConfig) as this = 

    inherit TypeProviderForNamespaces()

    let fbRuntimeInfo = FreebaseRuntimeInfo(config)

    /// Root namespace of Freebase types
    let rootNamespace = "FSharpx.TypeProviders.Freebase"
    let createTypes(apiKey, proxyPrefix, serviceUrl, rootTypeName, numIndividuals, useUnits, usePluralize, snapshotDate, useLocalCache, allowQueryEvaluateOnClientSide) = 

        let fb = new FreebaseQueries(apiKey, proxyPrefix, serviceUrl, "FreebaseSchema", snapshotDate, useLocalCache)
        let fbSchema = new FreebaseSchemaConnection(fb)
        let tidyName(value:string) = value.Replace("&amp;","&")

        let firstCap (s:string) = s.[0..0].ToUpperInvariant() + s.[1..]

        let typeNameForDomainObjects(domainName:string) = 
            // If the domain already contains spaces then we might as well make its type have a space before Domain since quoting will be required anyway.
            (if domainName.Contains(" ") then domainName+" Domain" else domainName+"Domain") |> tidyName

    #if NO_SECURITY_ELEMENT_ESCAPE
        let xmlDoc (text:string) = "<summary>" + text + "</summary>"
    #else
        let xmlDoc text = "<summary>" + System.Security.SecurityElement.Escape text + "</summary>"
    #endif

        let blurbOfId id = fbSchema.GetBlurbById id |> String.concat " "  

        let createDataContext = fbRuntimeInfo.FreebaseDataContextType.GetMethod("_Create")
        let getDomainCategoryById = fbRuntimeInfo.FreebaseDataContextType.GetMethod("_GetDomainCategoryById")
        let getDomainById = fbRuntimeInfo.FreebaseDomainCategoryType.GetMethod("_GetDomainById")
        let getObjectsOfTypeId = fbRuntimeInfo.FreebaseDomainType.GetMethod("_GetObjectsOfTypeId")
        let getIndividualsObject = fbRuntimeInfo.FreebaseIndividualsType.GetMethod("_GetIndividualsObject")
        let getIndividualById = fbRuntimeInfo.FreebaseIndividualsType.GetMethod("_GetIndividualById")

        let domains = 
            lazy 
                [ for nsk in fbSchema.GetDomainStructure().NamespaceKeys do 
                    if String.IsNullOrWhiteSpace nsk.Namespace.Hidden then
                     if nsk.Namespace.NamespaceKinds |> Array.exists (fun s -> s = "/type/domain") then
                      yield nsk.Namespace ]
                |> Seq.distinctBy (fun nsp -> (nsp.Id, nsp.DomainName))

        let domainCategories = lazy  fbSchema.GetDomainCategories() 

        let domainTypes = 
           lazy 
              let allRealDomains = 
                  [ for domain in domains.Force() do
                       yield (KnownDomain domain.Id, lazy (fbSchema.GetAllTypesInDomainSansProperties(domain.Id)) ) ]
              dict 
                [ yield! allRealDomains
                  yield (UnknownDomain, 
                         lazy 
                                let hashSet = HashSet()
                                for (_, lazyTypesInDomain) in allRealDomains do 
                                    for ty in lazyTypesInDomain.Force() do
                                       hashSet.Add ty.Id |> ignore
                                [ for ty in fbSchema.GetAllTypesInAllDomainsSansProperties() do
                                      if not (hashSet.Contains ty.Id) then
                                          yield ty ] :> seq<_>) ]

        let getDomains() =  domains.Force()
        let getDomainCategories() =  domainCategories.Force()
        let getTypesOfDomain(domainId) =  domainTypes.Force().[domainId].Force()

        let theServiceTypesClass = ProvidedTypeDefinition("ServiceTypes",baseType=Some typeof<obj>,HideObjectMethods=true)
        theServiceTypesClass.AddXmlDoc (xmlDoc "Contains the types defined in the web data store")

        let pluralize = if usePluralize then FSharpx.Strings.pluralize else id
    
        let containerTypeNameForDomainTypes (fbDomainIdOpt:DomainId) = 
            match fbDomainIdOpt with 
            | UnknownDomain -> "Uncategorized"
            | KnownDomain fbDomainId -> fbDomainId.TrimStart('/').Replace('/',' ') |> firstCap

        let pathToTypeForFreebaseTypeId (fbDomainId:string, fbTypeId:string) = 
            let domainPath = [containerTypeNameForDomainTypes  (match fbDomainId with null -> UnknownDomain | t -> KnownDomain t)]
            let path, final = fbTypeId.Split '/' |> List.ofArray |> List.frontAndBack 
            match path with
            | [] -> failwith "Unexpected 9078543"
            | [_hd] -> failwith "Unexpected 0984509"
            | _empty::path -> List.map firstCap  domainPath @ List.map firstCap path @ [firstCap final + "Data"]

        /// Given a description of a Freebase type, return the path where the corresponding provided type lives 
        /// under FreebaseData.DataTypes.
        let pathToTypeForFreebaseType (fbType:FreebaseType) = 
            pathToTypeForFreebaseTypeId (fbType.Domain, fbType.Id)

        /// Given a description of a Freebase type, find the corresponding provided type that lives 
        /// under FreebaseData.DataTypes.
        let tryFindTypeForFreebaseType (root:System.Type, path:string list) = 
            // The first fragment has theServiceTypesClass as parent
            (Some root, path) ||> List.fold (fun parent name -> 
                match parent with 
                | None -> None 
                | Some parent -> 
                    match parent.GetNestedType (name, BindingFlags.Public ||| BindingFlags.NonPublic) with 
                    | :? ProvidedTypeDefinition as p -> Some (p  :> System.Type)
                    | _ -> None ) 

        let tryFindRefinedTypeForFreebaseType fbType = 
            let path = pathToTypeForFreebaseType fbType 
            tryFindTypeForFreebaseType (theServiceTypesClass , path)

        let tryFindRefinedTypeForFreebaseTypeId fbTypeId = 
            let path = pathToTypeForFreebaseTypeId fbTypeId 
            tryFindTypeForFreebaseType (theServiceTypesClass , path)

        let tryFindRefinedCollectionTypeForFreebaseType fbType = 
            let path = pathToTypeForFreebaseType fbType 
            let f,b = List.frontAndBack path
            let path = f @ [b + "Collection"]
            tryFindTypeForFreebaseType (theServiceTypesClass, path)

        // PARAMETER: Unit normalization
        let refinedFSharpTypeOfFreebaseProperty (fp: FreebaseProperty) =
            // NOTE: if you alter this mapping, see GetProperty in FreebaseRuntime.fs
            match fp.ExpectedType with 
            | "/type/float" -> 
                match fp.UnitOfMeasure with 
                | u when useUnits && units.ContainsKey u -> 
                        let (measureAnnotation,_multipler,_offset) = units.[u]
                        let rec trans u = 
                            match u with 
                            | SI s -> ProvidedMeasureBuilder.Default.SI s
                            | Prod(u1,u2) -> ProvidedMeasureBuilder.Default.Product (trans u1, trans u2)
                            | Div(u1,u2) -> ProvidedMeasureBuilder.Default.Ratio (trans u1, trans u2)
                            | One -> ProvidedMeasureBuilder.Default.One
                        let floatWithMeasureAnnotation = ProvidedMeasureBuilder.Default.AnnotateType(typeof<double>,[trans measureAnnotation])
                        Some (floatWithMeasureAnnotation, false)
                | _ -> 
                    Some (typeof<double>, false)
            | _ -> None
        let makeDesignTimeNullableTy (ty:Type) = ProvidedTypeBuilder.MakeGenericType(typedefof<Nullable<_>>, [ ty ])
        let makeDesignTimeSeqTy (ty:Type) = ProvidedTypeBuilder.MakeGenericType(typedefof<seq<_>>, [ ty ])

        /// Given a description of a Freebase type, make the members for the corresponding provided type that lives 
        /// under FreebaseData.DataTypes.  
        let makeMembersForFreebaseType (fbType: FreebaseType) =
            [ let typeWithProperties = fbSchema.GetTypeByTypeId fbType.Id
              match typeWithProperties with
              | None -> ()
              | Some typeWithProperties -> 
                  for (property:FreebaseProperty) in typeWithProperties.Properties do
                    if not (String.IsNullOrEmpty property.PropertyName) then 
                        let staticPropertyType = property.FSharpPropertyType(fbSchema, refinedFSharpTypeOfFreebaseProperty, tryFindRefinedTypeForFreebaseType, makeDesignTimeNullableTy, makeDesignTimeSeqTy)
                        let runtimePropertyType = property.FSharpPropertyRuntimeType(fbSchema, fbRuntimeInfo.FreebaseObjectType)
                        let p = ProvidedProperty(property.PropertyName, staticPropertyType,
                                                 GetterCode = (fun args -> 
                                                      let meth = fbRuntimeInfo.FreebaseObjectType.GetMethod "GetPropertyByIdTyped"
                                                      let meth = meth.MakeGenericMethod [| runtimePropertyType |]
                                                      Expr.Call(args.[0],meth,[Expr.Value typeWithProperties.Id; Expr.Value property.Id])))

                        p.AddXmlDocDelayed(fun () -> blurbOfId property.Id |> xmlDoc)
                        yield (p :> MemberInfo) 
                     ]

        let insertFreebaseTypesForOneDomain (theDataTypesClassForDomain,domainId) = 
        
            //printfn "FreebaseProvider: inserting types for domain '%+A'" domainId
            let allTypesForDomainSansProperties = getTypesOfDomain domainId
            // Collect up the immediate nested types for the domain type
            let theNestedTypesForTheDataTypesClassForDomain = ResizeArray<_>()
            /// Holds all enclosing type definitions we've created so far in this domain.
            let enclosingTypeHash = Dictionary<(ProvidedTypeDefinition * string),ProvidedTypeDefinition>(HashIdentity.Structural)  
    
            /// Find an enclosing type on the path through the type space, create it if it is not created yet
            let findOrCreateEnclosingType (parentType:ProvidedTypeDefinition) name =
                let key = (parentType,name)
                match enclosingTypeHash.TryGetValue key with 
                | false,_ -> 
                    let t = ProvidedTypeDefinition(name, baseType=Some typeof<obj>,HideObjectMethods=true)
                    t.HideObjectMethods <- true
                    t.AddXmlDoc (xmlDoc "Contains a subset of the types defined in the web data store")
                    enclosingTypeHash.Add(key, t)
                    if Object.ReferenceEquals(parentType, theDataTypesClassForDomain) then 
                        theNestedTypesForTheDataTypesClassForDomain.Add t
                    else
                        parentType.AddMember t
                    t
                | _,t -> 
                    t

            for fbType in allTypesForDomainSansProperties do
                let fullPath = pathToTypeForFreebaseType fbType
                let path, typeName = List.frontAndBack fullPath
                let _domain, path = List.headAndTail path
                let declaringType = (theDataTypesClassForDomain, path) ||> List.fold findOrCreateEnclosingType
                //printfn "FreebaseProvider: creating item type, typeName='%A', fullPath='%A', domainId '%+A', declaringType.Name = '%s'" typeName fullPath domainId declaringType.Name
                let itemType = 
                    let t = ProvidedTypeDefinition(typeName, baseType=Some fbRuntimeInfo.FreebaseObjectType, HideObjectMethods=true)
                    t.SetAttributes (TypeAttributes.Public ||| TypeAttributes.Interface ||| enum (int32 TypeProviderTypeAttributes.IsErased))
                    t.AddInterfaceImplementationsDelayed(fun () -> [fbRuntimeInfo.FreebaseObjectType])
                    t.AddMembersDelayed (fun () -> makeMembersForFreebaseType fbType)
                    t.AddInterfaceImplementationsDelayed(fun () -> 
                      [ for ity in fbType.IncludedTypes do 
                          match tryFindRefinedTypeForFreebaseTypeId (ity.Domain, ity.Id) with 
                          | Some i -> yield i
                          | None -> 
                              //System.Diagnostics.Debug.Assert(false,"included type not found")
                              () ])

                    t.AddXmlDocDelayed (fun () -> blurbOfId fbType.Id |> xmlDoc)
                    t

                //printfn "FreebaseProvider: creating individuals type, typeName='%A', fullPath='%A', domainId '%+A', declaringType.Name = '%s'" typeName fullPath domainId declaringType.Name
                let individualsTypeOpt = 
                    if numIndividuals > 0 then 
                        let t = ProvidedTypeDefinition(itemType.Name + "Individuals", baseType=Some fbRuntimeInfo.FreebaseIndividualsType,HideObjectMethods=true)
                        t.HideObjectMethods <- true
                        t.AddXmlDoc (xmlDoc (sprintf "Represents a sample set of specific named individuals of type '%s' in the web data store" fbType.TypeName))
                        t.AddMembersDelayed(fun () -> 
                            [ for fbObj in fbSchema.GetAllObjectsOfType(fbType) |> Seq.truncate numIndividuals |> Seq.distinctBy (fun x -> x.ObjectName) do 
                                  let p = ProvidedProperty(fbObj.ObjectName, itemType,
                                                        GetterCode = (fun args -> Expr.Call(args.[0], getIndividualById,[Expr.Value fbType.Id;Expr.Value fbObj.Id])))
                                  p.AddXmlDocDelayed(fun () -> blurbOfId fbObj.Id |> xmlDoc)
                                  yield p
                            ])
                        Some t
                    else 
                        None

                //printfn "FreebaseProvider: creating collection type, typeName='%A', fullPath='%A', domainId '%+A', declaringType.Name = '%s'" typeName fullPath domainId declaringType.Name
                let collectionsType = 
                    let t = ProvidedTypeDefinition(itemType.Name + "Collection", baseType=Some typeof<obj>,HideObjectMethods=true)
                    t.HideObjectMethods <- true
                    t.AddInterfaceImplementationsDelayed (fun () -> [ ProvidedTypeBuilder.MakeGenericType(typedefof<IQueryable<_>>, [itemType :> System.Type]) ])
                    t.AddXmlDoc (xmlDoc (sprintf "Represents the collection of all individuals of type '%s' in the web data store" fbType.TypeName))                
                    t.AddMembersDelayed(fun () -> 

                        [ match individualsTypeOpt with 
                          | None -> ()
                          | Some individualsType -> 
                            let pIndividuals = 
                                ProvidedProperty("Individuals", individualsType,
                                                  GetterCode = (fun args -> Expr.Call(getIndividualsObject, [ Expr.Coerce(args.[0], typeof<obj>) ])))
                            pIndividuals.AddXmlDocDelayed(fun () -> sprintf "A sample set of named individuals of type '%s' in the web data store" fbType.TypeName |> xmlDoc)
                    
                            yield pIndividuals]
                        )
                    t

                //printfn "FreebaseProvider: adding types as members, typeName='%A', fullPath='%A', domainId '%+A', declaringType.Name = '%s'" typeName fullPath domainId declaringType.Name
                declaringType.AddMember itemType
                Option.iter declaringType.AddMember individualsTypeOpt
                declaringType.AddMember collectionsType

            //printfn "FreebaseProvider: done inserting types for domain '%+A'" domainId
            theNestedTypesForTheDataTypesClassForDomain |> Seq.toArray

        /// Lazily populate all the entries under FreebaseData.ServiceTypes. 
        do theServiceTypesClass.AddMembersDelayed (fun () -> 

            let makeTypeForFreebaseDomainTypes(domainId:DomainId, domainName:string) = 
                let domainTypeName = containerTypeNameForDomainTypes domainId
                let theDataTypesClassForDomain = ProvidedTypeDefinition(domainTypeName,baseType=Some typeof<obj>,HideObjectMethods=true)
                theDataTypesClassForDomain.AddXmlDoc (xmlDoc "Contains the types defined in the web data store for domain '" + domainName + "'")
                theDataTypesClassForDomain.AddMembersDelayed(fun () -> insertFreebaseTypesForOneDomain (theDataTypesClassForDomain,domainId) |> Array.toList) 
                theDataTypesClassForDomain

            [ for domain in getDomains() do 
                yield makeTypeForFreebaseDomainTypes (KnownDomain domain.Id, domain.DomainName)
              yield makeTypeForFreebaseDomainTypes (UnknownDomain, "Uncategorized") ] )

        /// Make the type that corresponds to a Freebase domain. The type lives under DomainObjects. Under FreebaseData you will
        /// also find a single property whose type is this domain type.
        let makeTypeForFreebaseDomainObjects(domainInfo: FreebaseDomain) = 
    
            let domainTypeName = typeNameForDomainObjects domainInfo.DomainName

            let t = ProvidedTypeDefinition(domainTypeName,baseType=Some fbRuntimeInfo.FreebaseDomainType,HideObjectMethods=true)
            t.AddXmlDocDelayed (fun () -> blurbOfId domainInfo.Id |> xmlDoc)
            t.HideObjectMethods <- true

            t.AddMembersDelayed(fun () -> 
    
                [ for childType in getTypesOfDomain (KnownDomain domainInfo.Id) do
                      // Note, don't include mediator types in the all-objects-categorized-by-type presentation
                      if not(String.IsNullOrEmpty(childType.TypeName)) && childType.Mediator <> "true" then
                          match tryFindRefinedCollectionTypeForFreebaseType childType with 
                          | None -> ()
                          | Some collectionType -> 
                        
                              let p = ProvidedProperty(pluralize childType.TypeName, collectionType, 
                                                        GetterCode = (fun args -> Expr.Call(args.[0],getObjectsOfTypeId,[Expr.Value childType.Id])))
                              if childType.Deprecated="true" then 
                                  p.AddObsoleteAttribute "This type is marked 'deprecated' in the data store"
                              p.AddXmlDocDelayed (fun () -> blurbOfId childType.Id |> xmlDoc) 
                              yield p ])
                    
            t

        /// Make the class that holds all the nested classes that hold all the objects in different Freebase domains categorized by type.
        let theDomainObjectsClass =
            let t = ProvidedTypeDefinition("DomainObjects",baseType=Some typeof<obj>,HideObjectMethods=true)
            t.AddXmlDoc (xmlDoc "Contains the domains of the domain categories defined in the web data store")
            t.AddMembersDelayed(fun () -> 
                        [ for domainInfo in getDomains() do
                              yield makeTypeForFreebaseDomainObjects domainInfo ]) 
            t

        let theServiceType = ProvidedTypeDefinition("FreebaseService",baseType=Some fbRuntimeInfo.FreebaseDataContextType, HideObjectMethods=true)
        do theServiceType.AddXmlDoc "Represents the information available in the web data store. See www.freebase.com for terms and conditions."

        /// Populate the root type (FreebaseData) with properties, one for each Freebase domain. Also include the DomainObjects 
        /// and DataTypes classes.
        do theServiceType.AddMembersDelayed (fun () -> 
            let c = getDomainCategories()
            [ for domainCategory in c do
                let domainCategoryName = domainCategory.Name.Replace("&amp;", "and")
                let t = ProvidedTypeDefinition(domainCategoryName,baseType=Some fbRuntimeInfo.FreebaseDomainCategoryType,HideObjectMethods=true)
                t.AddXmlDoc (xmlDoc (sprintf "Represents the objects of the domain category '%s' defined in the web data store organized by type" domainCategory.Name))
                t.AddMembersDelayed(fun () -> 
                    [ for domainInfo in domainCategory.Domains do
                          let domainName = domainInfo.DomainName
                          let domainTypeName = typeNameForDomainObjects domainName
                          let domainType = theDomainObjectsClass.GetNestedType (domainTypeName, BindingFlags.Public ||| BindingFlags.NonPublic)
                          let propertyName = tidyName domainName
                          let pi = ProvidedProperty(propertyName, domainType, IsStatic=false, 
                                                    GetterCode = (fun args -> Expr.Call(args.[0], getDomainById,[Expr.Value(domainInfo.Id)])))
                          pi.AddXmlDocDelayed (fun () -> blurbOfId domainInfo.Id |> xmlDoc)
                          yield pi]) 
                theDomainObjectsClass.AddMember t
                let domainCategoryIdVal = domainCategory.Id
                let p = ProvidedProperty(domainCategoryName, t, IsStatic=false, 
                                         GetterCode = (fun args -> Expr.Call(args.[0], getDomainCategoryById,[Expr.Value(domainCategoryIdVal)])))
                p.AddXmlDocDelayed (fun () -> xmlDoc (sprintf "Contains the objects of the domain category '%s' defined in the web data store organized by type" domainCategory.Name))
                yield p ])

        theServiceTypesClass.AddMembers [theServiceType; theDomainObjectsClass ]

        let theRootType = ProvidedTypeDefinition(fbRuntimeInfo.RuntimeAssembly,rootNamespace,rootTypeName,baseType=Some typeof<obj>, HideObjectMethods=true)
        theRootType.AddXmlDoc "Contains data and types drawn from the web data store. See www.freebase.com for terms and conditions."
        theRootType.AddMembers [ theServiceTypesClass  ]
        theRootType.AddMembersDelayed (fun () -> 
            [ yield ProvidedMethod ("GetDataContext", [], theServiceType, IsStaticMethod=true,
                                    InvokeCode = (fun _args -> Expr.Call(createDataContext, [  Expr.Value apiKey; Expr.Value proxyPrefix; Expr.Value serviceUrl; Expr.Value useUnits; Expr.Value snapshotDate; Expr.Value useLocalCache; Expr.Value allowQueryEvaluateOnClientSide  ])))
            ])
        theRootType

    let defaultNumIndividuals = 1000
    let defaultUseUnits = true
    let defaultPluralize = true
    let defaultSnapshotDate = "none"
    // By default we use the freebaseread API, as this supports cross-domain access
    //let defaultServiceUrl = "http://freebaseread.com/api"
    let defaultServiceUrl = "https://www.googleapis.com/freebase/v1"
    let defaultLocalSchemaCache = true
    let defaultApiKey = "none"
    let defaultAllowQueryEvaluateOnClientSide = true
#if BROWSER
    let defaultProxyUri = AsyncUtilities.RunOnMainThread(fun () -> System.Windows.Application.Current.Host.Source)
    let defaultProxyPrefix = defaultProxyUri.Scheme + "://" + defaultProxyUri.Host + "/proxy/csv?url="
#else
    let defaultProxyPrefix = "none"
#endif
    let freebaseType = createTypes(defaultApiKey, defaultProxyPrefix, defaultServiceUrl, "FreebaseData", defaultNumIndividuals, defaultUseUnits,defaultPluralize, defaultSnapshotDate, defaultLocalSchemaCache, defaultAllowQueryEvaluateOnClientSide)
    let paramFreebaseType   = ProvidedTypeDefinition(fbRuntimeInfo.RuntimeAssembly, rootNamespace, "FreebaseDataProvider", Some(typeof<obj>), HideObjectMethods = true)
    let apiKeyParam = ProvidedStaticParameter("Key",    typeof<string>, defaultApiKey)
    let numIndividualsParam = ProvidedStaticParameter("NumIndividuals",    typeof<int>, defaultNumIndividuals)
    let useUnitsParam       = ProvidedStaticParameter("UseUnitsOfMeasure", typeof<bool>,defaultUseUnits)
    let pluralizeParam      = ProvidedStaticParameter("Pluralize",         typeof<int>,defaultPluralize)
    let snapshotDateParam   = ProvidedStaticParameter("SnapshotDate",      typeof<string>, defaultSnapshotDate)
    let serviceUrlParam   = ProvidedStaticParameter("ServiceUrl",      typeof<string>, defaultServiceUrl)
    let localCacheParam   = ProvidedStaticParameter("LocalCache",      typeof<bool>, defaultLocalSchemaCache)
    let proxyPrefixParam   = ProvidedStaticParameter("ProxyPrefix",      typeof<string>, defaultProxyPrefix)
    let allowQueryEvaluateOnClientSideParam   = ProvidedStaticParameter("AllowQueryEvaluateOnClientSide",      typeof<bool>, defaultAllowQueryEvaluateOnClientSide)

    let helpText = "<summary>Typed representation of Freebase data with additional configuration parameters</summary>
                    <param name='Key'>The API key for the MQL metadata service (default: " + defaultApiKey + ")</param>
                    <param name='ServiceUrl'>The service URL for the MQL metadata service (default: " + defaultServiceUrl + ")</param>
                    <param name='NumIndividuals'>The maximum number of sample individuals for each Freebase type (default: " + string defaultNumIndividuals + ")</param>
                    <param name='UseUnitsOfMeasure'>Use the unit-of-measure annotations from the data source metadata (default: " + sprintf "%b" defaultUseUnits + ")</param>
                    <param name='Pluralize'>Use adhoc rules to pluralize the names of types when forming names of collections (default: " + sprintf "%b" defaultPluralize + ")</param>
                    <param name='SnapshotDate'>Use a snapshot of the web data store at the given date and/or time in ISO8601 format, e.g., 2012-01-18, 2012-09-15T21:11:32. A value of 'now' indicates the compile time of the code. (default: no snapshot)</param>
                    <param name='LocalCache'>Use a persistent local cache for schema requests. Also provides the default for whether a persistent local cache is used at runtime. A per-session cache is always used for schema data but it will not persist if this is set to 'false'. (default: true)</param>
                    <param name='AllowLocalQueryEvaluation'>Allow local evalution of some parts of a query. If false, then an exception will be raised if a query can't be evaluated fully on the server. If true, data sets may be implicitly brought to the client for processing. (default: " + (sprintf "%b" defaultAllowQueryEvaluateOnClientSide) + ")</param>"
    do paramFreebaseType.AddXmlDoc(helpText)
    do paramFreebaseType.DefineStaticParameters([apiKeyParam;serviceUrlParam;numIndividualsParam;useUnitsParam;pluralizeParam;snapshotDateParam;localCacheParam;proxyPrefixParam;allowQueryEvaluateOnClientSideParam], fun typeName providerArgs -> 
          let apiKey = (providerArgs.[0] :?> string)
          let serviceUrl = (providerArgs.[1] :?> string)
          let numIndividuals = (providerArgs.[2] :?> int)
          let useUnits = (providerArgs.[3] :?> bool)
          let usePluralize = (providerArgs.[4] :?> bool)
          let snapshotDate = (providerArgs.[5] :?> string)
          let snapshotDate = 
              match snapshotDate with 
              | "now" -> System.DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss") 
              | null | "" | "none"  -> snapshotDate
              | _ -> try ignore(System.DateTime.Parse(snapshotDate)); snapshotDate with e -> failwith ("invalid snapshot date" + e.Message)

          let useLocalCache = (providerArgs.[6] :?> bool)
          let proxyPrefix = (providerArgs.[7] :?> string)
          let allowQueryEvaluateOnClientSide =  (providerArgs.[8] :?> bool)
          createTypes(apiKey, proxyPrefix, serviceUrl, typeName, numIndividuals, useUnits, usePluralize, snapshotDate, useLocalCache, allowQueryEvaluateOnClientSide))
    do 
      this.AddNamespace(rootNamespace, [ freebaseType ])
      this.AddNamespace(rootNamespace, [ paramFreebaseType ])
