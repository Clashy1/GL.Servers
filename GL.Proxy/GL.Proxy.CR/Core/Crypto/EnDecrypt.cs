﻿namespace GL.Proxy.CR.Core.Crypto
{
    using System;
    using System.Linq;
    using GL.Proxy.CR.Core.Crypto.Encrypter;
    using GL.Proxy.CR.Core.Network;
    using GL.Proxy.CR.Logic.Enums;
    using GL.Servers.Library.TweetNaCl;

    public class EnDecrypt
    {
        private Status State    = Status.Disconnected;

        private byte[] SecretKey;
        private byte[] SessionKey;
        private byte[] ClientNonce;
        private byte[] ServerNonce;
        private byte[] ClientPublicKey;
        
        private byte[] ProxyPublicKey;
        private byte[] ProxySecretKey;

        private IEncrypter SendEncrypter1;
        private IEncrypter SendEncrypter2;
        private IEncrypter ReceiveEncrypter1;
        private IEncrypter ReceiveEncrypter2;

        private IEncrypter SendUDPEncrypter1;
        private IEncrypter SendUDPEncrypter2;
        private IEncrypter ReceiveUDPEncrypter1;
        private IEncrypter ReceiveUDPEncrypter2;

        private int PepperState;
        
        private enum Status
        {
            Disconnected
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="EnDecrypt"/> class.
        /// </summary>
        public EnDecrypt()
        {
            this.ProxyPublicKey = new byte[32];
            this.ProxySecretKey = new byte[32];

            Array.Copy(PepperFactory.SupercellServerPublicKey, this.ProxyPublicKey, 32);
            curve25519xsalsa20poly1305.crypto_box_keypair(this.ProxyPublicKey, this.ProxySecretKey);
        }

        /// <summary>
        /// Decrypts the specified message.
        /// </summary>
        /// <param name="Message">The message.</param>
        public byte[] Decrypt(Packet Message)
        {
            byte[] EncryptedData    = Message.Payload.ToArray();
            byte[] DecryptedData    = null;

            if (Message.Identifier == 24112)
            {
                Message.Identifier = 0;
            }

            if (this.ReceiveEncrypter1 == null)
            {
                if (this.PepperState == 0)
                {
                    if (Message.Identifier == 10100)
                    {
                        DecryptedData = PepperCrypto.PepperAuthentification(EncryptedData);
                    }
                }
                else
                {
                    switch (this.PepperState)
                    {
                        case 1:
                        {
                            DecryptedData = PepperCrypto.PepperAuthentificationResponseOpen(EncryptedData);

                            if (Message.Identifier == 20100)
                            {
                                int KeyL = DecryptedData[0] << 24 | DecryptedData[1] << 16 | DecryptedData[2] << 8 | DecryptedData[3];

                                if (KeyL != 24)
                                {
                                    Logging.Error(this.GetType(), "Decrypt() - Session key length is not valid. (" + KeyL + ")");
                                }

                                Array.Copy(DecryptedData, 4, this.SessionKey = new byte[KeyL], 0, KeyL);
                            }

                            break;
                        }
                        case 2:
                        {
                            if (Message.Identifier == 10101)
                            {
                                DecryptedData = PepperCrypto.PepperLoginOpen(PepperFactory.ServerPublicKey, PepperFactory.ServerSecretKey, this.SessionKey, ref this.ClientPublicKey, ref this.ClientNonce, EncryptedData);
                            }
                            else
                                goto default;

                            break;
                        }

                        case 3:
                        {
                            if (Message.Destination == Destination.FROM_SERVER)
                            {
                                DecryptedData = PepperCrypto.PepperLoginResponseOpen(this.ClientNonce, this.ProxyPublicKey, PepperFactory.SupercellServerPublicKey, this.ProxySecretKey, ref this.ServerNonce, ref this.SecretKey, EncryptedData);

                                this.ReceiveEncrypter1 = new PepperEncrypter(this.ClientNonce, this.SecretKey);
                                this.ReceiveEncrypter2 = new PepperEncrypter(this.ServerNonce, this.SecretKey);
                            }
                            else
                                goto default;

                            break;
                        }

                        default:
                        {
                            DecryptedData = EncryptedData;
                            break;
                        }
                    }
                }
            }
            else
            {
                if (Message.Destination == Destination.FROM_CLIENT)
                {
                    DecryptedData = this.ReceiveEncrypter1.Decrypt(EncryptedData);
                }
                else
                    DecryptedData = this.ReceiveEncrypter2.Decrypt(EncryptedData);
            }

            return DecryptedData;
        }

        /// <summary>
        /// Encrypts the specified message.
        /// </summary>
        /// <param name="Message">The message.</param>
        public byte[] Encrypt(Packet Message)
        {
            byte[] DecryptedData = Message.Decrypted_Data.ToArray();
            byte[] EncryptedData = Message.Decrypted_Data.ToArray();

            if (this.SendEncrypter1 == null)
            {
                if (this.PepperState == 0)
                {
                    if (Message.Identifier == 10100)
                    {
                        ++this.PepperState;
                        EncryptedData = PepperCrypto.PepperAuthentification(EncryptedData);
                    }
                }
                else
                {
                    switch (this.PepperState)
                    {
                        case 1:
                        {
                            this.PepperState = 2;
                            EncryptedData = PepperCrypto.PepperAuthentificationResponse(EncryptedData);

                            break;
                        }
                        case 2:
                        {
                            if (Message.Identifier == 10101)
                            {
                                this.PepperState = 3;
                                EncryptedData = PepperCrypto.PepperLogin(PepperFactory.SupercellServerPublicKey, this.ProxySecretKey, this.ProxyPublicKey, this.SessionKey, this.ClientNonce, DecryptedData);
                            }
                            else
                                goto default;

                            break;
                        }

                        case 3:
                        {
                            if (Message.Destination == Destination.FROM_SERVER)
                            {
                                this.PepperState = -1;

                                EncryptedData = PepperCrypto.PepperLoginResponse(this.ClientNonce, PepperFactory.ServerPublicKey, this.ClientPublicKey, PepperFactory.ServerSecretKey, this.ServerNonce, this.SecretKey, DecryptedData);

                                this.SendEncrypter1 = new PepperEncrypter(this.ClientNonce, this.SecretKey);
                                this.SendEncrypter2 = new PepperEncrypter(this.ServerNonce, this.SecretKey);
                            }
                            else
                                goto default;

                            break;
                        }

                        default:
                        {
                            EncryptedData = DecryptedData;
                            break;
                        }
                    }
                }
            }
            else
            {
                if (Message.Destination == Destination.FROM_CLIENT)
                {
                    EncryptedData = this.SendEncrypter1.Encrypt(DecryptedData);
                }
                else
                    EncryptedData = this.SendEncrypter2.Encrypt(DecryptedData);
            }
            
            return EncryptedData;
        }

        internal void StartUDP(string Nonce)
        {
            this.SendEncrypter1 = new RC4Encrypter(Nonce: Nonce);
            this.SendEncrypter2 = new RC4Encrypter(Nonce: Nonce);
            this.ReceiveEncrypter1 = new RC4Encrypter(Nonce: Nonce);
            this.ReceiveEncrypter2 = new RC4Encrypter(Nonce: Nonce);
        }
    }
}