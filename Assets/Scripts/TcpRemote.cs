/*
 * Author: Dongho Kang (kangd@ethz.ch)
 */

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using UnityEditor;
using UnityEngine;
using Quaternion = UnityEngine.Quaternion;
using Vector3 = UnityEngine.Vector3;

namespace raisimUnity
{
    // socket commands from client
    enum ServerMessageType : int
    {
        Initialization = 0,
        ObjectPositionUpdate,
        Status,
        NoMessage,
    }

    enum ClientMessageType : int
    {    
        RequestObjectPosition = 0,
        RequestInitialization,
        RequestResource,                 // request mesh, texture. etc files
        RequestChangeRealtimeFactor,
        RequestContactSolverDetails,
        RequestPause,
        RequestResume,
    }

    enum ServerStatus : int
    {
        StatusRendering = 0,
        StatusHibernating,
        StatusTerminating,
    }

    enum RsObejctType : int
    {
        RsSphereObject = 0, 
        RsBoxObject,
        RsCylinderObject,
        RsConeObject, 
        RsCapsuleObject,
        RsMeshObject,
        RsHalfSpaceObject, 
        RsCompoundObject,
        RsHeightMapObject,
        RsArticulatedSystemObject,
    }

    enum RsShapeType : int
    {
        RsBoxShape = 0, 
        RsCylinderShape,
        RsSphereShape,
        RsMeshShape,
        RsCapsuleShape, 
        RsConeShape,
    }

    static class VisualTag
    {
        public const string Visual = "visual";
        public const string Collision = "collision";
        public const string VisualAndCollision = "visual,collision";
    }

    public class TcpRemote : MonoBehaviour
    {
        // prevent repeated instances
        private static TcpRemote instance;
        private TcpRemote() {}
        public static TcpRemote Instance
        {
            get 
            {
                if( instance==null )
                {
                    instance = new TcpRemote();
                    return instance;
                }
                else
                    throw new System.Exception("TCPRemote can only be instantiated once");
            }
        }
        
        // script options
        private string tcpAddress = "127.0.0.1";
        private int tcpPort = 8080;

        // tcp client and stream
        private TcpClient _client = null;
        private NetworkStream _stream = null;
        
        // buffer
        // TODO get buffer spec from raisim
        private const int _maxBufferSize = 33554432;
        private const int _maxPacketSize = 4096;
        private const int _footerSize = sizeof(Byte);
        
        private byte[] _buffer;
        private float lastcheck = 0;
        
        // path 
        private string _resDirPath;

        // status
        private bool _tcpTryConnect = false;
        private bool _showVisualBody = true;
        private bool _showCollisionBody = false;

        void Start()
        {
            // set buffer size
            _buffer = new byte[_maxBufferSize];
            
            // TODO no hard coding!
            _resDirPath = Path.Combine(Application.dataPath, "Resources");
        }

        void Update()
        {
            // broken connection: clear
            if( !CheckConnection() )
            {
                // TODO connection lost popup
                ClearScene();
                
                _client = null;
                _stream = null;
            }

            // data available: handle communication
            if (_client != null && _client.Connected && _stream != null)
            {
                try
                {
                    UpdatePosition();
                }
                catch (Exception) {}
            }
            
            // escape
            if(Input.GetKey("escape"))
                Application.Quit();
        }

        private void ClearScene()
        {
            foreach (Transform objT in gameObject.transform)
            {
                Destroy(objT.gameObject);
            }
        }

        private int InitializeScene()
        {
            int offset = 0;
            
            if (!_stream.CanWrite)
                return -1;
            
            Byte[] data = BitConverter.GetBytes((int) ClientMessageType.RequestInitialization);
            _stream.Write(data, 0, data.Length);

            if (ReadData() == 0)
                return -1;

            ServerStatus state = BitIO.GetData<ServerStatus>(ref _buffer, ref offset);
            
            if (state == ServerStatus.StatusTerminating)
                return 0;

            ServerMessageType messageType = BitIO.GetData<ServerMessageType>(ref _buffer, ref offset);

            ulong configurationNumber = BitIO.GetData<ulong>(ref _buffer, ref offset);

            ulong numObjects = BitIO.GetData<ulong>(ref _buffer, ref offset);

            for (ulong i = 0; i < numObjects; i++)
            {
                ulong objectIndex = BitIO.GetData<ulong>(ref _buffer, ref offset);
                
                RsObejctType objectType = BitIO.GetData<RsObejctType>(ref _buffer, ref offset);
                
                switch (objectType) 
                {
                    case RsObejctType.RsSphereObject :
                    {
                        float radius = BitIO.GetData<float>(ref _buffer, ref offset);
                        ObjectController.CreateSphere(gameObject, objectIndex.ToString(), radius, VisualTag.VisualAndCollision);
                    }
                        break;

                    case RsObejctType.RsBoxObject :
                    {
                        float sx = BitIO.GetData<float>(ref _buffer, ref offset);
                        float sy = BitIO.GetData<float>(ref _buffer, ref offset);
                        float sz = BitIO.GetData<float>(ref _buffer, ref offset);
                        ObjectController.CreateBox(gameObject, objectIndex.ToString(), sx, sy, sz, VisualTag.VisualAndCollision);
                    }
                        break;
                    case RsObejctType.RsCylinderObject:
                    {
                        float radius = BitIO.GetData<float>(ref _buffer, ref offset);
                        float height = BitIO.GetData<float>(ref _buffer, ref offset);
                        ObjectController.CreateCylinder(gameObject, objectIndex.ToString(), radius, height, VisualTag.VisualAndCollision);
                    }
                        break;
                    case RsObejctType.RsCapsuleObject:
                    {
                        float radius = BitIO.GetData<float>(ref _buffer, ref offset);
                        float height = BitIO.GetData<float>(ref _buffer, ref offset);
                        ObjectController.CreateCapsule(gameObject, objectIndex.ToString(), radius, height, VisualTag.VisualAndCollision);
                    }
                        break;
                    case RsObejctType.RsMeshObject:
                    {
                        string meshFile = BitIO.GetData<string>(ref _buffer, ref offset);
                        string meshFileName = Path.GetFileNameWithoutExtension(meshFile);
                        string directoryName = Path.GetFileName(Path.GetDirectoryName(meshFile));
                        ObjectController.CreateMesh(gameObject, objectIndex.ToString(), Path.Combine(directoryName, meshFileName), 1.0f, 1.0f, 1.0f, VisualTag.VisualAndCollision);
                    }
                        break;
                    case RsObejctType.RsHalfSpaceObject:
                    {
                        float height = BitIO.GetData<float>(ref _buffer, ref offset);
                        ObjectController.CreateHalfSpace(gameObject, objectIndex.ToString(), height, VisualTag.VisualAndCollision);
                    }
                        break;
                    case RsObejctType.RsHeightMapObject:
                    {
                        // center
                        float centerX = BitIO.GetData<float>(ref _buffer, ref offset);
                        float centerY = BitIO.GetData<float>(ref _buffer, ref offset);
                        // size
                        float sizeX = BitIO.GetData<float>(ref _buffer, ref offset);
                        float sizeY = BitIO.GetData<float>(ref _buffer, ref offset);
                        // num samples
                        ulong numSampleX = BitIO.GetData<ulong>(ref _buffer, ref offset);
                        ulong numSampleY = BitIO.GetData<ulong>(ref _buffer, ref offset);
                        ulong numSample = BitIO.GetData<ulong>(ref _buffer, ref offset);
                        
                        // height values 
                        float[,] heights = new float[numSampleY, numSampleX];
                        for (ulong j = 0; j < numSampleY; j++)
                        {
                            for (ulong k = 0; k < numSampleX; k++)
                            {
                                float height = BitIO.GetData<float>(ref _buffer, ref offset);
                                heights[j, k] = height;
                            }
                        }

                        ObjectController.CreateTerrain(gameObject, objectIndex.ToString(), numSampleX, sizeX, centerX, numSampleY, sizeY, centerY, heights, tag);
                    }
                        break;
                    case RsObejctType.RsArticulatedSystemObject:
                    {
                        string urdfDirPathInServer = BitIO.GetData<string>(ref _buffer, ref offset); 
                        string urdfDirName = Path.GetFileName(urdfDirPathInServer);
                        string urdfDirPathInClient = Path.Combine(Path.GetFullPath(_resDirPath), urdfDirName);
                        if (!Directory.Exists(urdfDirPathInClient))
                        {
                            // TODO error
                            print("no urdf dir");
                        }

                        // visItem = 0 (visuals)
                        // visItem = 1 (collisions)
                        for (int visItem = 0; visItem < 2; visItem++)
                        {
                            ulong numberOfVisObjects = BitIO.GetData<ulong>(ref _buffer, ref offset);

                            for (ulong j = 0; j < numberOfVisObjects; j++)
                            {
                                RsShapeType shapeType = BitIO.GetData<RsShapeType>(ref _buffer, ref offset);
                                
                                ulong group = BitIO.GetData<ulong>(ref _buffer, ref offset);

                                string subName = Path.Combine(objectIndex.ToString(), visItem.ToString(), j.ToString());
                                string tag = VisualTag.VisualAndCollision;

                                if (visItem == 0)
                                    tag = VisualTag.Visual;
                                else if (visItem == 1)
                                    tag = VisualTag.Collision;

                                if (shapeType == RsShapeType.RsMeshShape)
                                {
                                    string meshFile = BitIO.GetData<string>(ref _buffer, ref offset);
                                    string meshFileName = Path.GetFileName(meshFile);

                                    double sx = BitIO.GetData<double>(ref _buffer, ref offset);
                                    double sy = BitIO.GetData<double>(ref _buffer, ref offset);
                                    double sz = BitIO.GetData<double>(ref _buffer, ref offset);

                                    string meshFilePathInResources = Path.Combine(urdfDirName, Path.GetFileNameWithoutExtension(meshFileName));
                                    ObjectController.CreateMesh(gameObject, subName, meshFilePathInResources, (float)sx, (float)sy, (float)sz, tag);
                                }
                                else
                                {
                                    ulong size = BitIO.GetData<ulong>(ref _buffer, ref offset);
                                    
                                    var visParam = new List<double>();
                                    for (ulong k = 0; k < size; k++)
                                    {
                                        double visSize = BitIO.GetData<double>(ref _buffer, ref offset);
                                        visParam.Add(visSize);
                                    }
                                    switch (shapeType)
                                    {
                                        case RsShapeType.RsBoxShape:
                                        {
                                            if (visParam.Count != 3) throw new Exception("Box Mesh error");
                                            ObjectController.CreateBox(gameObject, subName, (float) visParam[0], (float) visParam[1], (float) visParam[2], tag);
                                        }
                                            break;
                                        case RsShapeType.RsCapsuleShape:
                                        {
                                            if (visParam.Count != 2) throw new Exception("Capsule Mesh error");
                                            ObjectController.CreateCapsule(gameObject, subName, (float)visParam[0], (float)visParam[1], tag);
                                        }
                                            break;
                                        case RsShapeType.RsConeShape:
                                        {
                                            // TODO URDF does not support cone shape
                                        }
                                            break;
                                        case RsShapeType.RsCylinderShape:
                                        {
                                            if (visParam.Count != 2) throw new Exception("Cylinder Mesh error");
                                            ObjectController.CreateCylinder(gameObject, subName, (float)visParam[0], (float)visParam[1], tag);
                                        }
                                            break;
                                        case RsShapeType.RsSphereShape:
                                        {
                                            if (visParam.Count != 1) throw new Exception("Sphere Mesh error");
                                            ObjectController.CreateSphere(gameObject, subName, (float)visParam[0], tag);
                                        }
                                            break;
                                    }
                                }
                            }
                        }
                    }
                        break;
                }
            }
            
            return 0;
        }

        private int UpdatePosition()
        {
            int offset = 0;

            // request object position
            Byte[] data = BitConverter.GetBytes((int) ClientMessageType.RequestObjectPosition);
            _stream.Write(data, 0, data.Length);
            if (!_stream.DataAvailable)
                return -1;
            
            if (ReadData() == 0)
                return -1;

            ServerStatus state = BitIO.GetData<ServerStatus>(ref _buffer, ref offset);
            
            if (state == ServerStatus.StatusTerminating)
                return 0;

            ServerMessageType messageType = BitIO.GetData<ServerMessageType>(ref _buffer, ref offset);
            if (messageType == ServerMessageType.NoMessage)
            {
                return -1;
            }
            
            ulong configurationNumber = BitIO.GetData<ulong>(ref _buffer, ref offset);

            ulong numObjects = BitIO.GetData<ulong>(ref _buffer, ref offset);

            for (ulong i = 0; i < numObjects; i++)
            {
                ulong localIndexSize = BitIO.GetData<ulong>(ref _buffer, ref offset);

                for (ulong j = 0; j < localIndexSize; j++)
                {
                    string objectName = BitIO.GetData<string>(ref _buffer, ref offset);
                    
                    double posX = BitIO.GetData<double>(ref _buffer, ref offset);
                    double posY = BitIO.GetData<double>(ref _buffer, ref offset);
                    double posZ = BitIO.GetData<double>(ref _buffer, ref offset);
                    
                    double quatW = BitIO.GetData<double>(ref _buffer, ref offset);
                    double quatX = BitIO.GetData<double>(ref _buffer, ref offset);
                    double quatY = BitIO.GetData<double>(ref _buffer, ref offset);
                    double quatZ = BitIO.GetData<double>(ref _buffer, ref offset);

                    GameObject localObject = GameObject.Find(objectName);

                    if (localObject != null)
                    {
                        ObjectController.SetTransform(
                            localObject, 
                            new Vector3((float)posX, (float)posY, (float)posZ), 
                            new Quaternion((float)quatX, (float)quatY, (float)quatZ, (float)quatW)
                        );
                    }
                    else
                    {
                        // TODO error
                        print("local object is null");
                    }
                }
            }

            return 0;
        }
        
        public void EstablishConnection()
        {
            try
            {
                // create tcp client and stream
                if (_client == null || !_client.Connected)
                {
                    _client = new TcpClient(tcpAddress, tcpPort);
                    _stream = _client.GetStream();
                }
            }
            catch (Exception e)
            {
                // connection cannot be established
                throw new RsuTcpConnectionException(e.Message);
            }

            try
            {
                // initialize scene when connection available
                if (_client != null && _client.Connected && _stream != null)
                {
                    InitializeScene();
                    
                    // show / hide objects
                    ShowOrHideObject();

                    // disable other cameras than main camera
                    foreach (var cam in Camera.allCameras)
                    {
                        if (cam == Camera.main) continue;
                        cam.enabled = false;
                    }
                }
            }
            catch (Exception e)
            {
                // connection cannot be established
                throw new RsuInitException(e.Message);
            }
        }

        public void CloseConnection()
        {
            try
            {
                // clear scene
                ClearScene();
                
                // clear tcp stream and client
                if (_stream != null)
                {
                    _stream.Close();
                    _stream = null;
                }

                if (_client != null)
                {
                    _client.Close();
                    _client = null;
                }
            }
            catch (Exception)
            {
            }
        }
        
        private bool CheckConnection()
        {
            try
            {
                if( _client!=null && _client.Client!=null && _client.Client.Connected )
                {
                    if( _client.Client.Poll(0, SelectMode.SelectRead) )
                    {
                        if( _client.Client.Receive(_buffer, SocketFlags.Peek)==0 )
                            return false;
                        else
                            return true;
                    }
                    else
                        return true;
                }
                else
                    return false;
            }
            catch
            {
                return false;
            }
        }
        
        private int ReadData()
        {
            int offset = 0;
            Byte footer = Convert.ToByte('c');
            while (footer == Convert.ToByte('c'))
            {
                int valread = _stream.Read(_buffer, offset, _maxPacketSize);
                if (valread == 0) break;
                footer = _buffer[offset + _maxPacketSize - _footerSize];
                offset += valread - _footerSize;
            }
            return offset;
        }
        
        void OnApplicationQuit()
        {
            // close tcp client
            if (_stream != null) _stream.Close();
            if (_client != null) _client.Close();
        }
        
        public void ShowOrHideObject()
        {
            foreach (var obj in GameObject.FindGameObjectsWithTag(VisualTag.Visual))
            {
                foreach (var collider in obj.GetComponentsInChildren<Collider>())
                    collider.enabled = _showVisualBody;
                foreach (var renderer in obj.GetComponentsInChildren<Renderer>())
                    renderer.enabled = _showVisualBody;
            }

            foreach (var obj in GameObject.FindGameObjectsWithTag(VisualTag.Collision))
            {
                foreach (var collider in obj.GetComponentsInChildren<Collider>())
                    collider.enabled = _showCollisionBody;
                foreach (var renderer in obj.GetComponentsInChildren<Renderer>())
                    renderer.enabled = _showCollisionBody;                
            }

            foreach (var obj in GameObject.FindGameObjectsWithTag(VisualTag.VisualAndCollision))
            {
                foreach (var collider in obj.GetComponentsInChildren<Collider>())
                    collider.enabled = _showVisualBody || _showCollisionBody;
                foreach (var renderer in obj.GetComponentsInChildren<Renderer>())
                    renderer.enabled = _showVisualBody || _showCollisionBody;
            }
        }
        
        // getters and setters
        public bool ShowVisualBody
        {
            get => _showVisualBody;
            set => _showVisualBody = value;
        }

        public bool ShowCollisionBody
        {
            get => _showCollisionBody;
            set => _showCollisionBody = value;
        }

        public string TcpAddress
        {
            get => tcpAddress;
            set => tcpAddress = value;
        }

        public int TcpPort
        {
            get => tcpPort;
            set => tcpPort = value;
        }

        public bool TcpTryConnect
        {
            get => _tcpTryConnect;
            set => _tcpTryConnect = value;
        }

        public bool TcpConnected
        {
            get => _client != null && _client.Connected;
        }
    }
}